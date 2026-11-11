"""Junta todo o código-fonte do projeto num único TXT.

Uso: python3 -I graphify.py <raiz_do_projeto> <arquivo_de_saida>
"""
import subprocess, os, sys, datetime
root = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else ".")
out = os.path.abspath(os.path.expanduser(sys.argv[2] if len(sys.argv) > 2 else "~/Downloads/codigo_fonte_app.txt"))
try:
    r = subprocess.run(["git", "ls-files"], cwd=root, capture_output=True, text=True, check=True)
    files = r.stdout.split("\n")
except (OSError, subprocess.CalledProcessError):
    files = []
    for d, dirs, names in os.walk(root):
        dirs[:] = [x for x in dirs if x not in {".git", "node_modules", "bin", "obj", "dist", "build", "out", ".vs", "__pycache__", ".venv", "venv"}]
        files += [os.path.relpath(os.path.join(d, n), root).replace(os.sep, "/") for n in names]
files = sorted(f for f in files if f and os.path.abspath(os.path.join(root, f)) != out)
bin_ext = {".png",".jpg",".jpeg",".gif",".ico",".pdf",".zip",".mp4",".mp3",".ttf",".woff",".woff2",".bmp",".sys",".dll",".cat",".exe",".otf"}
skip_names = {"package-lock.json","yarn.lock","pnpm-lock.yaml","Gemfile.lock","Podfile.lock","packages.lock.json",".DS_Store"}
skip_dirs = (".git/","node_modules/","dist/","build/","out/","bin/","obj/",".vs/",".next/","coverage/","__pycache__/",".venv/","venv/","TestResults/")
inc, exc = [], []
for f in filter(None, files):
    name = os.path.basename(f); ext = os.path.splitext(f)[1].lower()
    reason = None
    if ext in bin_ext: reason="binário"
    elif name in skip_names or (name.startswith(".env") and name != ".env.example") or ext==".log" or f.endswith((".min.js",".map")): reason="lock/log/minificado"
    elif any(("/"+f).find("/"+d)>=0 for d in skip_dirs): reason="pasta excluída"
    elif f.startswith("Drivers/") and ext==".inf": reason="driver de terceiros (vendor)"
    elif f.startswith("Assets/Fonts/"): reason="fonte/licença de fonte"
    else:
        data = open(os.path.join(root,f),"rb").read()
        if b"\0" in data[:8192]: reason="binário"
    (exc if reason else inc).append((f,reason))
sep = "="*80
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(out,"w",encoding="utf-8",newline="\n") as o:
    o.write(f"CÓDIGO-FONTE DO PROJETO: {os.path.basename(root)}\nGerado em: {datetime.date.today()}\nTotal de arquivos incluídos: {len(inc)}\n\n{sep}\nÍNDICE\n{sep}\n")
    for i,(f,_) in enumerate(inc,1): o.write(f"{i:4d}. {f}\n")
    o.write("\nArquivos excluídos (não-texto/terceiros):\n")
    for f,r in exc: o.write(f"  - {f} [{r}]\n")
    o.write("\n\n")
    for f,_ in inc:
        t = open(os.path.join(root,f),"rb").read().decode("utf-8-sig",errors="replace").replace("\r\n","\n")
        o.write(f"{sep}\nARQUIVO: {f}\n{sep}\n{t}")
        if not t.endswith("\n"): o.write("\n")
        o.write(f"\n--- FIM DO ARQUIVO: {f} ---\n\n\n")
print(f"{out}: {len(inc)} arquivos incluídos, {len(exc)} excluídos, {os.path.getsize(out)/1048576:.1f} MB")
