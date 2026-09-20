using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using ReplyFive.Desktop.Services;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ReplyFive.Desktop.Platform.Windows;

/// <summary>付録BP：画面の文字認識による会話の読み取り。LINE のように会話を UI Automation へ出さないアプリ向けの最後の手段。
/// ショートカット押下時（と付録BU の変化検知時）に会話の領域を 1 回だけ撮って文字にし、画像はその場で捨てる。保存も送信もしない。
/// Windows は画面の取り込みに許可が要らないので、使えるかどうかは OCR の言語が入っているかだけで決まる。</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static partial class WinOcr
{
    [GeneratedRegex(@"^(既読|Read)(\s*\d+)?\s+")] private static partial Regex ReadMark();

    static readonly object gate = new();
    static OcrEngine? engine;
    static bool probed;

    /// <summary>この端末で文字認識が使えるか（言語が 1 つも入っていなければ使えない）。</summary>
    internal static bool Available => Engine() is not null;

    static OcrEngine? Engine()
    {
        lock (gate)
        {
            if (probed) return engine;
            probed = true;
            try
            {
                engine = OcrEngine.TryCreateFromUserProfileLanguages()
                    ?? TryLanguage("ja") ?? TryLanguage("en");
                if (engine is null) Diag.Log("ocr unavailable (no recognizer language)");
            }
            catch (Exception e) { Diag.Log("ocr unavailable " + e.GetType().Name); engine = null; }
            return engine;
        }
    }

    static OcrEngine? TryLanguage(string tag)
    {
        try { return OcrEngine.TryCreateFromLanguage(new Language(tag)); }
        catch (Exception) { return null; }
    }

    // MARK: - 画面の取り込み

    /// <summary>画面の一部を取り込む。呼び出し側は必ず Dispose すること（画像は保存しない）。</summary>
    static Bitmap? Grab(Rectangle rect)
    {
        var screen = Native.VirtualScreen();
        if (screen.Width > 0) rect = Rectangle.Intersect(rect, screen);
        if (rect.Width < 8 || rect.Height < 8) return null;
        try
        {
            var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch (Exception e) { Diag.Log("screen grab failed " + e.GetType().Name); return null; }
    }

    /// <summary>読み取る領域を決める。region はウインドウ内の会話の列、exclude は入力欄（そこより上だけを読む）。</summary>
    static Rectangle? Area(IntPtr hwnd, Rectangle? region, Rectangle? exclude, Rectangle windowRect)
    {
        var bounds = windowRect.IsEmpty && hwnd != IntPtr.Zero ? Native.WindowRect(hwnd) : windowRect;
        var r = region ?? bounds;
        if (r.IsEmpty) return null;
        if (!bounds.IsEmpty)
        {
            r = Rectangle.Intersect(r, bounds);
            // 指定した会話領域がこのウインドウに無い。全体を読むとトーク一覧が混ざるので読まない
            if (r.Width < 40 || r.Height < 40) { Diag.Log("screen crop skipped (region outside window)"); return null; }
        }
        if (exclude is { } ex && ex.IntersectsWith(r) && ex.Top > r.Top) r = r with { Height = ex.Top - r.Top };
        return r.Width >= 40 && r.Height >= 40 ? r : null;
    }

    static SoftwareBitmap? ToSoftwareBitmap(Bitmap bmp)
    {
        try
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Bmp);
            var bytes = ms.ToArray();
            var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            if (!writer.StoreAsync().AsTask().Wait(2000)) return null;
            writer.FlushAsync().AsTask().Wait(1000);
            writer.DetachStream();
            stream.Seek(0);
            var decode = BitmapDecoder.CreateAsync(stream).AsTask();
            if (!decode.Wait(3000)) return null;
            var soft = decode.Result.GetSoftwareBitmapAsync().AsTask();
            return soft.Wait(3000) ? soft.Result : null;
        }
        catch (Exception e) { Diag.Log("ocr decode failed " + e.GetType().Name); return null; }
    }

    // MARK: - 文字認識

    /// <summary>会話の領域を 1 回だけ撮って文字にする。読み取り順は上から下、左から右。失敗・使えないときは null。</summary>
    internal static string? Read(IntPtr hwnd, Rectangle? region, Rectangle? exclude, Rectangle windowRect, double budgetSeconds = 0.6)
    {
        var ocr = Engine();
        if (ocr is null) return null;
        if (Area(hwnd, region, exclude, windowRect) is not { } area) return null;
        using var bmp = Grab(area);
        if (bmp is null) return null;
        var width = bmp.Width;
        SoftwareBitmap? soft = null;
        try
        {
            soft = ToSoftwareBitmap(bmp);
            if (soft is null) return null;
            var task = ocr.RecognizeAsync(soft).AsTask();
            // 予算より少し長く待つ（macOS 版と同じく、遅れた結果は捨てる）
            if (!task.Wait(TimeSpan.FromSeconds(budgetSeconds + 1.0))) return null;
            var result = task.Result;
            if (result is null || result.Lines.Count == 0) return null;
            return Compose(result, width);
        }
        catch (Exception e) { Diag.Log("ocr failed " + e.GetType().Name); return null; }
        finally { soft?.Dispose(); }   // 画像はここで捨てる
    }

    /// <summary>付録BS：吹き出し型（LINE 等）では右寄せの行が自分の発言。左寄せの行もあるときだけ判定し、「自分: 」を付ける。</summary>
    static string? Compose(OcrResult result, int imageWidth)
    {
        if (imageWidth <= 0) return null;
        var lines = new List<(double Top, double MinX, double MaxX, string Text)>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            var text = line.Text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            double top = 0, minX = double.MaxValue, maxX = 0;
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                top += r.Y;
                minX = Math.Min(minX, r.X);
                maxX = Math.Max(maxX, r.X + r.Width);
            }
            lines.Add((top / line.Words.Count, minX / imageWidth, maxX / imageWidth, text));
        }
        if (lines.Count == 0) return null;
        lines.Sort((a, b) => a.Top.CompareTo(b.Top));
        var hasLeft = lines.Any(l => l.MinX < 0.3);
        var composed = new List<string>(lines.Count);
        foreach (var l in lines)
        {
            var body = l.Text;
            var own = hasLeft && l.MinX > 0.4 && l.MaxX > 0.85;
            // LINE の「既読」「既読 3」は自分の吹き出しの脇に付く
            var m = ReadMark().Match(body);
            if (m.Success) { body = body[m.Length..]; own = true; }
            if (body.Length == 0) continue;
            composed.Add(own ? "自分: " + body : body);
        }
        var joined = string.Join("\n", composed).Trim();
        if (joined.Length == 0) return null;
        return joined.Length > WinAccessibility.MaxChars ? joined[^WinAccessibility.MaxChars..] : joined;
    }

    // MARK: - 変化検知の署名（付録BU）

    /// <summary>会話の領域を 32×32 のグレーに縮めたハッシュ。文字認識（0.5〜1 秒）を、画面が変わったときだけ走らせるために使う。</summary>
    internal static int? Signature(IntPtr hwnd, Rectangle? region)
    {
        if (Engine() is null) return null;
        var bounds = Native.WindowRect(hwnd);
        if (bounds.IsEmpty) return null;
        var area = region is { } r ? Rectangle.Intersect(r, bounds) : bounds;
        if (area.Width < 8 || area.Height < 8) return null;
        using var full = Grab(area);
        if (full is null) return null;
        try
        {
            const int size = 32;
            using var small = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                g.DrawImage(full, new Rectangle(0, 0, size, size));
            }
            var data = small.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[data.Stride * size];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var hash = 5381;
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var i = y * data.Stride + x * 4;
                        // 輝度にしてから下位ビットを捨てる（微妙な描画差を吸収）
                        var gray = (bytes[i + 2] * 77 + bytes[i + 1] * 150 + bytes[i] * 29) >> 8;
                        hash = ((hash << 5) + hash) ^ (gray >> 3);
                    }
                }
                return hash;
            }
            finally { small.UnlockBits(data); }
        }
        catch (Exception e) { Diag.Log("screen signature failed " + e.GetType().Name); return null; }
    }
}
