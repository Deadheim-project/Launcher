#!/usr/bin/env python3
"""Deadheim no Linux, com o Valheim NATIVO (valheim.x86_64). Sem Wine, sem Proton, sem .NET.

Faz o mesmo que o Deadheim Launcher do Windows: lê o manifest.json do servidor,
baixa os mods nas versões fixadas, monta uma árvore BepInEx própria (fora da pasta
do jogo, que não é tocada) e abre o Valheim com o Doorstop apontado para ela.

Uso:
    ./deadheim.py jogar                  baixa/atualiza os mods e abre o jogo
    ./deadheim.py opcionais              escolhe os mods opcionais (menu)
    ./deadheim.py opcionais --listar     mostra o que está ligado
    ./deadheim.py opcionais --ativar gizmo,vnei
    ./deadheim.py opcionais --desativar gizmo
    ./deadheim.py instalar               só baixa/atualiza, sem abrir o jogo
    ./deadheim.py config --valheim /caminho/Valheim --senha SENHA
    ./deadheim.py log                    fim dos logs da última partida (para a staff)
    ./deadheim.py limpar                 apaga os mods baixados (config e senha ficam)

A Steam precisa estar aberta e logada.
"""
import argparse
import getpass
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

MANIFEST_URL = "https://raw.githubusercontent.com/Deadheim-project/Launcher/main/manifest.json"
APPID = "892970"
EXECUTAVEL = "valheim.x86_64"
HEXIUM = "https://valheim.hexium.gg"
UA = "DeadheimLauncher-Linux/1.0"

CONFIG_DIR = Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config") / "deadheim"
DATA_DIR = Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share") / "deadheim"
CONFIG_FILE = CONFIG_DIR / "config.json"
MANIFEST_CACHE = DATA_DIR / "manifest.json"
PERFIL = DATA_DIR / "perfil"
CACHE = DATA_DIR / "cache"
BEPINEX = PERFIL / "BepInEx"
REGISTRO = BEPINEX / ".deadheim" / "instalados.json"
LOG_JOGO = PERFIL / "valheim_linux.log"

# O manifest não declara dependências. O VNEI precisa destes dois (ver _comment do manifest).
DEPENDENCIAS = {"vnei": ["jotunn", "yamldotnet"]}

# Todo pacote do Thunderstore traz estes na raiz do zip; não fazem parte do mod.
METADADOS = {"manifest.json", "icon.png", "readme.md", "changelog.md", "license", "license.md", "license.txt"}

VARIAVEIS_DO_WINE = ("WINEPREFIX", "WINELOADER", "WINEDLLPATH", "WINEDLLOVERRIDES", "WINESERVER", "WINEDEBUG")


def erro(msg):
    print(f"ERRO: {msg}", file=sys.stderr)
    sys.exit(1)


# --- config -----------------------------------------------------------------

def carregar_config():
    try:
        return json.loads(CONFIG_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def salvar_config(cfg):
    CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    CONFIG_FILE.write_text(json.dumps(cfg, indent=2, ensure_ascii=False), encoding="utf-8")
    try:
        CONFIG_FILE.chmod(0o600)  # guarda a senha do servidor
    except OSError:
        pass


# --- rede -------------------------------------------------------------------

def http_get(url, tentativas=3):
    ultimo = None
    for i in range(tentativas):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "application/json, */*"})
            with urllib.request.urlopen(req, timeout=60) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            ultimo = e
            if e.code in (404, 403):  # não adianta repetir
                break
        except (urllib.error.URLError, TimeoutError) as e:
            ultimo = e
        time.sleep(1 + i * 2)
    raise RuntimeError(f"falha ao baixar {url}: {ultimo}")


def http_json(url):
    return json.loads(http_get(url).decode("utf-8"))


# --- manifest ---------------------------------------------------------------

def carregar_manifest(cfg):
    url = cfg.get("manifest_url") or MANIFEST_URL
    try:
        bruto = http_get(url)
        manifest = json.loads(bruto.decode("utf-8-sig"))
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        MANIFEST_CACHE.write_bytes(bruto)
        return manifest
    except (RuntimeError, ValueError) as e:
        if MANIFEST_CACHE.exists():
            print(f"Sem rede para o manifest ({e}); usando a cópia salva.")
            return json.loads(MANIFEST_CACHE.read_text(encoding="utf-8-sig"))
        erro(f"não consegui ler o manifest: {e}")


def todos_os_mods(manifest):
    mods = list(manifest.get("ownMods", [])) + list(manifest.get("thunderstoreMods", []))
    for m in mods:
        m.setdefault("required", False)
        m.setdefault("category", "Servidor" if m["required"] else "Opcional")
        m.setdefault("target", "Plugins")
    return mods


def opcionais_do_manifest(mods):
    return [m for m in mods if not m["required"]]


def selecionados(mods, cfg):
    """Obrigatórios + opcionais ligados + dependências dos ligados."""
    por_id = {m["id"]: m for m in mods}
    ligados = set(cfg.get("opcionais", []))
    for i in list(ligados):
        ligados.update(DEPENDENCIAS.get(i, []))
    return [m for m in mods if m["required"] or (m["id"] in ligados and m["id"] in por_id)]


# --- resolução de versão ----------------------------------------------------

def resolver(mod):
    """Devolve (versao, url_do_zip)."""
    fonte = mod.get("source")
    versao = mod.get("version")
    ns, nome = mod.get("thunderstoreNamespace"), mod.get("thunderstoreName")

    if fonte == "GitHub":
        dono, repo, padrao = mod.get("gitHubOwner"), mod.get("gitHubRepo"), mod.get("assetPattern") or ".zip"
        if versao and padrao.lower().endswith(".zip"):
            tag = versao if versao.startswith("v") else "v" + versao
            return versao, f"https://github.com/{dono}/{repo}/releases/download/{tag}/{padrao}"
        rel = http_json(f"https://api.github.com/repos/{dono}/{repo}/releases/latest")
        for a in rel.get("assets", []):
            if padrao.lower() in a["name"].lower():
                return rel["tag_name"].lstrip("v"), a["browser_download_url"]
        raise RuntimeError(f"nenhum asset de {dono}/{repo} bate com '{padrao}'")

    if fonte == "Thunderstore":
        if versao:
            return versao, f"https://thunderstore.io/package/download/{ns}/{nome}/{versao}/"
        ultimo = http_json(f"https://thunderstore.io/api/experimental/package/{ns}/{nome}/")["latest"]
        return ultimo["version_number"], ultimo["download_url"]

    if fonte == "Hexium":
        url = f"{HEXIUM}/api/experimental/package/{ns}/{nome}/" + (f"{versao}/" if versao else "")
        dados = http_json(url)
        dados = dados.get("latest", dados)
        if not dados.get("download_url"):
            raise RuntimeError(f"o Hexium não devolveu download_url para {ns}/{nome}")
        return dados.get("version_number", versao or "?"), dados["download_url"]

    raise RuntimeError(f"fonte desconhecida '{fonte}' no mod {mod['id']}")


# --- instalação -------------------------------------------------------------

def extrair(zip_path, destino):
    """Extrai trocando '\\' por '/': alguns zips do Thunderstore são feitos no Windows."""
    destino = Path(destino)
    destino.mkdir(parents=True, exist_ok=True)
    raiz = destino.resolve()
    with zipfile.ZipFile(zip_path) as z:
        for info in z.infolist():
            nome = info.filename.replace("\\", "/")
            if not nome or nome.endswith("/"):
                continue
            alvo = (destino / nome).resolve()
            if raiz != alvo and raiz not in alvo.parents:
                raise RuntimeError(f"zip com caminho suspeito: {info.filename}")
            alvo.parent.mkdir(parents=True, exist_ok=True)
            with z.open(info) as src, open(alvo, "wb") as dst:
                shutil.copyfileobj(src, dst)


def desembrulhar_pasta_unica(pasta):
    """Zip todo dentro de uma única pasta (ex.: BepInExPack_Valheim/): sobe o conteúdo um nível."""
    pasta = Path(pasta)
    subpastas = [p for p in pasta.iterdir() if p.is_dir()]
    arquivos = [p for p in pasta.iterdir() if p.is_file()]
    if len(subpastas) != 1 or any(a.name.lower() not in METADADOS for a in arquivos):
        return
    for a in arquivos:
        a.unlink()
    interna = subpastas[0]
    for item in list(interna.iterdir()):
        alvo = pasta / item.name
        if alvo.exists():
            shutil.rmtree(alvo) if alvo.is_dir() else alvo.unlink()
        shutil.move(str(item), str(alvo))
    interna.rmdir()


def mesclar(origem, destino):
    shutil.copytree(origem, destino, dirs_exist_ok=True)


def carregar_registro():
    try:
        return json.loads(REGISTRO.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def salvar_registro(reg):
    REGISTRO.parent.mkdir(parents=True, exist_ok=True)
    REGISTRO.write_text(json.dumps(reg, indent=2), encoding="utf-8")


def ja_instalado(mod, versao, reg):
    if reg.get(mod["id"]) != versao:
        return False
    if mod["target"] == "GameRoot":
        return (BEPINEX / "core").is_dir()
    if mod["target"] == "BepInExRoot":
        return True
    return (BEPINEX / "plugins" / mod["id"]).is_dir()


def instalar_mod(mod, versao, url):
    CACHE.mkdir(parents=True, exist_ok=True)
    zip_path = CACHE / f"{mod['id']}-{versao}.zip"
    if not zip_path.exists():
        parcial = zip_path.with_suffix(".part")
        parcial.write_bytes(http_get(url))
        parcial.rename(zip_path)

    alvo = mod["target"]
    if alvo == "Plugins":
        destino = BEPINEX / "plugins" / mod["id"]
        if destino.exists():
            shutil.rmtree(destino)
        extrair(zip_path, destino)
        # Os pacotes do servidor trazem os .cfg com os valores reais: o BepInEx só os lê em BepInEx/config.
        cfg_do_mod = destino / "config"
        if cfg_do_mod.is_dir():
            mesclar(cfg_do_mod, BEPINEX / "config")
            shutil.rmtree(cfg_do_mod)
        return

    with tempfile.TemporaryDirectory(prefix="deadheim-montagem-") as tmp:
        extrair(zip_path, tmp)
        desembrulhar_pasta_unica(tmp)
        mesclar(tmp, PERFIL if alvo == "GameRoot" else BEPINEX)
    legado = BEPINEX / "plugins" / mod["id"]
    if legado.exists():
        shutil.rmtree(legado)


def instalar(cfg, manifest):
    mods = todos_os_mods(manifest)
    escolhidos = selecionados(mods, cfg)
    reg = carregar_registro()
    PERFIL.mkdir(parents=True, exist_ok=True)

    # O BepInEx primeiro: os outros pacotes se montam em cima dele.
    escolhidos.sort(key=lambda m: m["target"] != "GameRoot")

    for i, mod in enumerate(escolhidos, 1):
        prefixo = f"[{i}/{len(escolhidos)}] {mod.get('name', mod['id'])}"
        try:
            versao, url = resolver(mod)
            if ja_instalado(mod, versao, reg):
                print(f"{prefixo}: {versao} (já instalado)")
                continue
            print(f"{prefixo}: baixando {versao}...")
            instalar_mod(mod, versao, url)
            reg[mod["id"]] = versao
            salvar_registro(reg)
        except (RuntimeError, OSError, zipfile.BadZipFile) as e:
            erro(f"{mod['id']}: {e}")

    # Opcional desligado some do perfil, para o jogo não carregar o que você não pediu.
    manter = {m["id"] for m in escolhidos}
    for mod in opcionais_do_manifest(mods):
        pasta = BEPINEX / "plugins" / mod["id"]
        if mod["id"] not in manter and mod["target"] == "Plugins" and pasta.exists():
            print(f"Removendo opcional desligado: {mod.get('name', mod['id'])}")
            shutil.rmtree(pasta)
            reg.pop(mod["id"], None)
    salvar_registro(reg)

    if not (PERFIL / "doorstop_libs" / "libdoorstop_x64.so").exists():
        erro("o BepInExPack baixado não trouxe doorstop_libs/libdoorstop_x64.so (Doorstop de Linux).")
    if not (BEPINEX / "core" / "BepInEx.Preloader.dll").exists():
        erro("BepInEx.Preloader.dll não está no perfil; rode 'limpar' e tente de novo.")
    print("Mods em dia.")


# --- localizar o Valheim ----------------------------------------------------

def bibliotecas_steam():
    raizes = [
        Path.home() / ".local/share/Steam",
        Path.home() / ".steam/steam",
        Path.home() / ".steam/root",
        Path.home() / ".var/app/com.valvesoftware.Steam/.local/share/Steam",
        Path.home() / "snap/steam/common/.local/share/Steam",
    ]
    achadas = []
    for raiz in raizes:
        if raiz.exists():
            achadas.append(raiz)
        vdf = raiz / "steamapps" / "libraryfolders.vdf"
        try:
            texto = vdf.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for caminho in re.findall(r'"path"\s+"([^"]+)"', texto):
            achadas.append(Path(caminho.replace("\\\\", "\\")))
    vistas, unicas = set(), []
    for p in achadas:
        real = str(p.resolve()) if p.exists() else str(p)
        if real not in vistas:
            vistas.add(real)
            unicas.append(p)
    return unicas


def achar_valheim(cfg):
    informado = cfg.get("valheim_path")
    if informado:
        pasta = Path(informado).expanduser()
        if (pasta / EXECUTAVEL).exists():
            return pasta
        if (pasta / "valheim.exe").exists():
            erro(f"'{pasta}' é a versão do Windows (valheim.exe). Aqui precisa da nativa: na Steam, "
                 "tire o Proton do Valheim (Propriedades > Compatibilidade) e instale de novo.")
        erro(f"não achei {EXECUTAVEL} em '{pasta}'. Confira com: deadheim.py config --valheim CAMINHO")
    for biblioteca in bibliotecas_steam():
        pasta = biblioteca / "steamapps" / "common" / "Valheim"
        if (pasta / EXECUTAVEL).exists():
            return pasta
    erro("não achei o Valheim nativo nas bibliotecas da Steam. Informe a pasta com: "
         "deadheim.py config --valheim /caminho/steamapps/common/Valheim")


# --- opcionais --------------------------------------------------------------

def menu_opcionais(cfg, manifest):
    mods = opcionais_do_manifest(todos_os_mods(manifest))
    ligados = set(cfg.get("opcionais", []))
    while True:
        print("\nMods opcionais (os obrigatórios do servidor sempre são instalados):\n")
        for n, m in enumerate(mods, 1):
            marca = "x" if m["id"] in ligados else " "
            print(f"  {n:2}. [{marca}] {m.get('name', m['id']):<32} {m['category']}")
        print("\nDigite os números para ligar/desligar (ex.: 1 3 5), 't' liga todos, 'n' desliga todos,")
        resposta = input("Enter para salvar e sair: ").strip().lower()
        if not resposta:
            break
        if resposta == "t":
            ligados = {m["id"] for m in mods}
        elif resposta == "n":
            ligados = set()
        else:
            for token in resposta.replace(",", " ").split():
                if token.isdigit() and 1 <= int(token) <= len(mods):
                    i = mods[int(token) - 1]["id"]
                    ligados.symmetric_difference_update({i})
    cfg["opcionais"] = sorted(ligados)
    salvar_config(cfg)
    print("Salvo. O próximo 'jogar' (ou 'instalar') aplica.")


def cmd_opcionais(args):
    cfg = carregar_config()
    manifest = carregar_manifest(cfg)
    mods = opcionais_do_manifest(todos_os_mods(manifest))
    validos = {m["id"] for m in mods}
    ligados = set(cfg.get("opcionais", []))

    def ids(texto):
        pedidos = [t.strip() for t in texto.split(",") if t.strip()]
        for p in pedidos:
            if p not in validos:
                erro(f"opcional desconhecido '{p}'. Ids válidos: {', '.join(sorted(validos))}")
        return set(pedidos)

    alterou = False
    if args.todos:
        ligados, alterou = set(validos), True
    if args.nenhum:
        ligados, alterou = set(), True
    if args.ativar:
        ligados |= ids(args.ativar)
        alterou = True
    if args.desativar:
        ligados -= ids(args.desativar)
        alterou = True

    if alterou:
        cfg["opcionais"] = sorted(ligados)
        salvar_config(cfg)
    if args.listar or alterou:
        for m in mods:
            marca = "x" if m["id"] in ligados else " "
            print(f"[{marca}] {m['id']:<32} {m.get('name', m['id'])} ({m['category']})")
        if ligados & set(DEPENDENCIAS):
            extras = sorted({d for i in ligados for d in DEPENDENCIAS.get(i, [])} - ligados)
            if extras:
                print(f"(dependências que entram junto: {', '.join(extras)})")
        return
    if not sys.stdin.isatty():
        erro("sem terminal para o menu; use --listar, --ativar ou --desativar.")
    menu_opcionais(cfg, manifest)


# --- jogar ------------------------------------------------------------------

def steam_aberta():
    try:
        return subprocess.run(["pgrep", "-x", "steam"], capture_output=True).returncode == 0
    except OSError:
        return True  # sem pgrep, não dá para saber; segue


def pedir_senha(cfg):
    if cfg.get("senha"):
        return cfg["senha"]
    if not sys.stdin.isatty():
        erro("falta a senha do servidor: deadheim.py config --senha SENHA")
    senha = getpass.getpass("Senha do servidor: ").strip()
    if not senha:
        erro("senha vazia.")
    if input("Guardar a senha neste computador? (s/N) ").strip().lower() == "s":
        cfg["senha"] = senha
        salvar_config(cfg)
    return senha


def cmd_jogar(args):
    cfg = carregar_config()
    manifest = carregar_manifest(cfg)
    jogo = achar_valheim(cfg)
    host = cfg.get("servidor_host") or manifest.get("serverHost")
    porta = cfg.get("servidor_porta") or manifest.get("serverPort")
    if not host or not porta:
        erro("o manifest não traz o endereço do servidor.")
    senha = pedir_senha(cfg)

    if not args.sem_atualizar:
        instalar(cfg, manifest)
    elif not (BEPINEX / "core").exists():
        erro("os mods ainda não foram baixados; rode sem --sem-atualizar uma vez.")

    if not steam_aberta():
        print("AVISO: não vi a Steam aberta. Abra e faça login nela primeiro, senão o jogo não inicia.")

    env = os.environ.copy()
    for v in VARIAVEIS_DO_WINE:
        env.pop(v, None)
    # Aberto fora da Steam, o jogo pede para a Steam reabri-lo sem estas duas, e o BepInEx fica para trás.
    env["SteamAppId"] = APPID
    env["SteamGameId"] = APPID
    preloader = str(BEPINEX / "core" / "BepInEx.Preloader.dll")
    env["DOORSTOP_ENABLED"] = "1"
    env["DOORSTOP_TARGET_ASSEMBLY"] = preloader
    env["DOORSTOP_IGNORE_DISABLED_ENV"] = "0"
    corlib = PERFIL / "unstripped_corlib"
    if corlib.is_dir():
        env["DOORSTOP_CORLIB_OVERRIDE_PATH"] = str(corlib)
    libs = str(PERFIL / "doorstop_libs")
    env["LD_LIBRARY_PATH"] = libs + (":" + env["LD_LIBRARY_PATH"] if env.get("LD_LIBRARY_PATH") else "")
    env["LD_PRELOAD"] = "libdoorstop_x64.so" + (":" + env["LD_PRELOAD"] if env.get("LD_PRELOAD") else "")

    comando = [str(jogo / EXECUTAVEL), "+connect", f"{host}:{porta}", "-password", senha]
    PERFIL.mkdir(parents=True, exist_ok=True)
    with open(LOG_JOGO, "wb") as log:
        proc = subprocess.Popen(comando, cwd=jogo, env=env, stdout=log, stderr=subprocess.STDOUT,
                                start_new_session=True)
    print(f"Valheim aberto (pid {proc.pid}). Saída do jogo: {LOG_JOGO}")
    time.sleep(4)
    if proc.poll() is not None:
        print(f"O jogo fechou logo ao abrir (código {proc.returncode}). Fim do log:\n")
        mostrar_fim(LOG_JOGO, 30)
        sys.exit(1)


# --- demais comandos --------------------------------------------------------

def mostrar_fim(arquivo, linhas):
    try:
        texto = Path(arquivo).read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        print(f"(sem {arquivo})")
        return
    print("\n".join(texto[-linhas:]))


def cmd_log(_args):
    print(f"--- {LOG_JOGO}")
    mostrar_fim(LOG_JOGO, 40)
    print(f"\n--- {BEPINEX / 'LogOutput.log'}")
    mostrar_fim(BEPINEX / "LogOutput.log", 60)


def cmd_config(args):
    cfg = carregar_config()
    if args.valheim:
        cfg["valheim_path"] = str(Path(args.valheim).expanduser())
    if args.senha is not None:
        cfg["senha"] = args.senha
    if args.manifest:
        cfg["manifest_url"] = args.manifest
    salvar_config(cfg)
    mostrado = {k: ("***" if k == "senha" and v else v) for k, v in cfg.items()}
    print(json.dumps(mostrado, indent=2, ensure_ascii=False))


def cmd_instalar(_args):
    cfg = carregar_config()
    instalar(cfg, carregar_manifest(cfg))


def cmd_limpar(args):
    alvo = DATA_DIR if args.tudo else BEPINEX / "plugins"
    if alvo.exists():
        shutil.rmtree(alvo)
    if not args.tudo and REGISTRO.exists():
        REGISTRO.unlink()
    print("Mods apagados. O próximo 'jogar' baixa tudo de novo. A pasta do Valheim não foi tocada.")


def main():
    p = argparse.ArgumentParser(description="Deadheim no Linux nativo.")
    sub = p.add_subparsers(dest="cmd", required=True)

    j = sub.add_parser("jogar", help="atualiza os mods e abre o Valheim")
    j.add_argument("--sem-atualizar", action="store_true", help="abre direto, sem checar versões")
    j.set_defaults(fn=cmd_jogar)

    sub.add_parser("instalar", help="só baixa/atualiza os mods").set_defaults(fn=cmd_instalar)

    o = sub.add_parser("opcionais", help="escolhe os mods opcionais")
    o.add_argument("--listar", action="store_true")
    o.add_argument("--ativar", metavar="ID,ID")
    o.add_argument("--desativar", metavar="ID,ID")
    o.add_argument("--todos", action="store_true")
    o.add_argument("--nenhum", action="store_true")
    o.set_defaults(fn=cmd_opcionais)

    c = sub.add_parser("config", help="caminho do jogo, senha, manifest")
    c.add_argument("--valheim", metavar="PASTA")
    c.add_argument("--senha")
    c.add_argument("--manifest", metavar="URL")
    c.set_defaults(fn=cmd_config)

    sub.add_parser("log", help="mostra os logs da última partida").set_defaults(fn=cmd_log)

    l = sub.add_parser("limpar", help="apaga os mods baixados")
    l.add_argument("--tudo", action="store_true", help="apaga também cache e perfil inteiro")
    l.set_defaults(fn=cmd_limpar)

    args = p.parse_args()
    try:
        args.fn(args)
    except KeyboardInterrupt:
        sys.exit(130)


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    main()
