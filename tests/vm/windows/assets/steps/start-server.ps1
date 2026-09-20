$env:REPLYFIVE_STORE = "memory"; $env:LLM_PROVIDER = "mock"; $env:ADMIN_BOOTSTRAP_TOKEN = "dev"; $env:SESSION_SECRET = "dev"; $env:PORT = "8787"; $env:HOST = "127.0.0.1"
Start-Process -FilePath C:\rf\replyfive-server.exe -ArgumentList serve -WindowStyle Hidden -RedirectStandardError C:\rf\server.err -RedirectStandardOutput C:\rf\server.out
"started"
