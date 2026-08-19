#!/usr/bin/env python3
"""
Compara o manifest.json (fonte de verdade autoral, editado à mão) com o que
está publicado no pack Deadheim do Thunderstore.

Por que virou um checador e não um gerador: o manifest declara as versões que
o SERVIDOR roda de fato (BepInEx/plugins/ em loboda.dathost.net), não as
versões mais recentes do pack. Gerar o manifest a partir do pack publicado
foi a causa de um desalinhamento real -- o pack levou a versão do Jotunn (e
de outros mods) na frente do servidor, o manifest seguiu o pack, e jogadores
levavam "Incompatible version" ao entrar. Editar o manifest à mão, com o
servidor como referência, evita isso: publicar o pack não muda mais o que o
launcher instala.

Fluxo pra atualizar um mod, agora:
  1. Sobe a versão nova em BepInEx/plugins/ no servidor (com backup).
  2. Confirma que o servidor sobe e aceita conexão normalmente.
  3. Só então edita manifest.json à mão, fixando essa versão.
  4. (Opcional) publica a versão nova do pack no Thunderstore, pra quem usa
     outro mod manager -- isso é downstream do manifest, não o contrário.

Uso:
    python installer/generate-manifest.py --check
        Mostra onde o pack publicado diverge do manifest.json atual. Não
        escreve nada. Saída 1 se houver divergência, 0 se bater tudo.

    python installer/generate-manifest.py --write [--out ARQUIVO]
        Bootstrap explícito: reescreve manifest.json (e manifest.sample.json)
        a partir do pack publicado, do zero. Uso raro -- normalmente só pra
        começar um manifest novo do nada. Confirma antes de rodar isso contra
        um manifest que já está em produção.
"""

import argparse
import json
import sys
import urllib.request

PACK_NAMESPACE = "Deadheimmods"
PACK_NAME = "Deadheim"
API = "https://thunderstore.io/api/experimental/package/{ns}/{name}/"
UA = {"User-Agent": "DeadheimLauncher-manifest-generator"}

MANIFEST_PATH = "manifest.json"

# O pacote do BepInEx não é um plugin: ele é o carregador, e vai na raiz da
# pasta do Valheim (winhttp.dll ao lado do valheim.exe).
GAME_ROOT_PACKAGES = {"BepInExPack_Valheim"}

# Mods que ainda constam do pack publicado mas sairam do servidor. Ficam listados
# aqui, e nao removidos silenciosamente, porque o pack no Thunderstore continua
# declarando a dependencia: sem esta lista, o check acusaria "faltando" à toa.
#
# Marketplace_And_Server_NPCs_Revamped: substituido pelo mod de NPCs proprio
# (Deadheim-project/npcs). Removido do servidor em 18/08/2026; o conteudo dele --
# 248 quests, 54 mercadores, 59 quadros de missao, 2 redes de teleporte -- foi
# importado e agora acompanha o nosso mod.
EXCLUDED_PACKAGES = {"Marketplace_And_Server_NPCs_Revamped", "FastLink"}

# Mods de conveniência/admin que rodam só no cliente: podem ficar de fora sem
# quebrar a entrada no servidor, então entram como opcionais e não são
# comparados por versão (não precisam bater com o servidor).
OPTIONAL_PACKAGES = {
    "Server_devcommands",
    "DevToggle",
    "Azus_UnOfficial_ConfigManager",
}

# Mods de autoria própria, que não vêm do Thunderstore e sim de GitHub Releases.
OWN_MODS_OWNER = "Deadheim-project"
OWN_MODS_AUTHOR = "Detalhes"
# (repositório, nome, descrição, tag do release, nome do arquivo)
#
# Tag e arquivo são declarados de propósito: com eles a URL de download é
# montada direto, sem consultar a API do GitHub. Sem versão fixada, o launcher
# precisava perguntar "qual é o último release?" para cada mod a cada partida —
# 5 chamadas que estouram o limite de 60/hora de quem não usa credencial e
# fazem o download falhar com 403.
OWN_MODS = [
    ("npcs", "NPCs", "Mercador, Teleportador, Correio e Missões.", "v1.0.0", "Npcs.zip"),
    ("Deadheim", "Deadheim", "Mod base do servidor.", "v1.0.0", "Deadheim.zip"),
    ("RaidSystem", "Raid System", "Sistema de raides.", "v1.0.0", "RaidSystem.zip"),
    ("Hearthstone", "Hearthstone", "Pedra de retorno.", "v1.0.0", "Hearthstone.zip"),
    ("donationshop", "Donation Shop", "Loja de doações.", "v1.0.0", "DonationShop.zip"),
]

ADMIN_MODS = [
    ("JereKuusela", "Server_devcommands",
     "Liga os devcommands e o teleporte que funciona de verdade. Base das outras ferramentas."),
    ("JereKuusela", "Infinity_Hammer",
     "Copia, cola e move qualquer estrutura ou objeto do mundo."),
    ("JereKuusela", "World_Edit_Commands",
     "Comandos de edição do mundo: terreno, objetos e vegetação em área."),
    ("JereKuusela", "Upgrade_World",
     "Regenera áreas já exploradas para receber conteúdo novo. CUIDADO: altera o mundo salvo."),
    ("JereKuusela", "Structure_Tweaks",
     "Deixa estruturas invisíveis, invulneráveis ou atravessáveis."),
    ("YouDied", "DevToggle",
     "Atalho para ligar e desligar o modo dev."),
    ("Azumatt", "Azus_UnOfficial_ConfigManager",
     "Menu dentro do jogo para ajustar a configuração dos mods, com F1."),
]

OPTIONAL_MODS = [
    ("MSchmoecker", "VNEI",
     "Mostra todos os itens e receitas do jogo numa janela de consulta."),
    ("ComfyMods", "Gizmo",
     "Permite girar peças em qualquer eixo na hora de construir."),
    ("castix", "ValheimFPSBoost",
     "Reduz alguns detalhes gráficos internos para ganhar FPS. O ganho varia por máquina."),
]


def fetch(url):
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.load(r)


def parse_dependency(dep):
    """'Azumatt-AzuClock-1.0.5' -> ('Azumatt', 'AzuClock', '1.0.5')"""
    parts = dep.rsplit("-", 2)
    if len(parts) != 3:
        raise ValueError(f"dependência em formato inesperado: {dep}")
    return parts[0], parts[1], parts[2]


def slugify(name):
    return name.lower().replace("_", "-").replace(" ", "-")


def build_thunderstore_mods_from_pack(deps):
    """Reconstroi a lista de mods do pack publicado, no mesmo formato do manifest."""
    thunderstore_mods = []
    for dep in deps:
        ns, name, version = parse_dependency(dep)
        if name in EXCLUDED_PACKAGES:
            continue
        entry = {
            "id": slugify(name),
            "name": name.replace("_", " "),
            "description": f"Do modpack Deadheim.",
            "required": name not in OPTIONAL_PACKAGES,
            "source": "Thunderstore",
            "thunderstoreNamespace": ns,
            "thunderstoreName": name,
            "version": version,
            "author": ns,
            "url": f"https://thunderstore.io/c/valheim/p/{ns}/{name}/",
        }
        if name in GAME_ROOT_PACKAGES:
            entry["target"] = "GameRoot"
            entry["description"] = "Carregador de mods do Valheim. Instalado na raiz do jogo."
        thunderstore_mods.append(entry)

    for ns, name, desc in ADMIN_MODS:
        if any(m["thunderstoreName"] == name for m in thunderstore_mods):
            continue
        thunderstore_mods.append({
            "id": slugify(name), "name": name.replace("_", " "), "description": desc,
            "required": False, "category": "Admin", "source": "Thunderstore",
            "thunderstoreNamespace": ns, "thunderstoreName": name, "author": ns,
            "url": f"https://thunderstore.io/c/valheim/p/{ns}/{name}/",
        })

    for ns, name, desc in OPTIONAL_MODS:
        if any(m["thunderstoreName"] == name for m in thunderstore_mods):
            continue
        thunderstore_mods.append({
            "id": slugify(name), "name": name.replace("_", " "), "description": desc,
            "required": False, "category": "Opcional", "source": "Thunderstore",
            "thunderstoreNamespace": ns, "thunderstoreName": name, "author": ns,
            "url": f"https://thunderstore.io/c/valheim/p/{ns}/{name}/",
        })

    return thunderstore_mods


def own_mods_entries():
    return [
        {
            "id": slugify(repo), "name": nome, "description": desc, "required": True,
            "source": "GitHub", "gitHubOwner": OWN_MODS_OWNER, "gitHubRepo": repo,
            "assetPattern": arquivo, "version": tag.lstrip("v"), "author": OWN_MODS_AUTHOR,
            "url": f"https://github.com/{OWN_MODS_OWNER}/{repo}/releases",
        }
        for repo, nome, desc, tag, arquivo in OWN_MODS
    ]


def cmd_check():
    try:
        with open(MANIFEST_PATH, encoding="utf-8") as f:
            current = json.load(f)
    except FileNotFoundError:
        print(f"faltando {MANIFEST_PATH} -- nada pra comparar", file=sys.stderr)
        return 1

    pack = fetch(API.format(ns=PACK_NAMESPACE, name=PACK_NAME))
    latest = pack["latest"]
    pack_version = latest["version_number"]
    pack_mods = build_thunderstore_mods_from_pack(latest["dependencies"])
    pack_by_name = {m["thunderstoreName"]: m for m in pack_mods}

    current_mods = {m["thunderstoreName"]: m for m in current.get("thunderstoreMods", [])}

    print(f"Pack publicado: {PACK_NAMESPACE}/{PACK_NAME} v{pack_version}")
    print(f"Manifest atual: {MANIFEST_PATH} (packVersion registrado: {current.get('packVersion', '?')})")
    print()

    divergiu = False

    for name, pack_mod in pack_by_name.items():
        cur = current_mods.get(name)
        pack_ver = pack_mod.get("version")
        if pack_ver is None:
            continue  # mod sem versão fixada no pack (admin/opcional) -- não comparável
        if cur is None:
            print(f"  [NO MANIFEST] {name}: pack tem v{pack_ver}, manifest não lista esse mod")
            divergiu = True
            continue
        cur_ver = cur.get("version")
        if cur_ver != pack_ver:
            print(f"  [DIVERGE] {name}: manifest fixa v{cur_ver}, pack publicado está em v{pack_ver}")
            divergiu = True

    if not divergiu:
        print("Nenhuma divergência de versão entre o manifest e o pack publicado.")
        return 0

    print()
    print("Isso é só um aviso -- o pack publicado não é a fonte de verdade do launcher.")
    print("Se o servidor já roda a versão nova, atualize manifest.json à mão.")
    print("Se não, ignore: o manifest reflete o servidor, não o pack.")
    return 1


def cmd_write(out_path):
    print("!! --write reescreve manifest.json inteiro a partir do pack publicado.")
    print("!! Isso by-passa qualquer versão fixada à mão para refletir o servidor.")
    print("!! Uso esperado: bootstrap de um manifest novo, não manutenção de rotina.")

    pack = fetch(API.format(ns=PACK_NAMESPACE, name=PACK_NAME))
    latest = pack["latest"]
    pack_version = latest["version_number"]
    thunderstore_mods = build_thunderstore_mods_from_pack(latest["dependencies"])
    own_mods = own_mods_entries()

    manifest = {
        "_comment": (
            "Fonte de verdade autoral: edite à mão. As versões aqui devem bater "
            "com o que roda em BepInEx/plugins/ no servidor de producao "
            "(loboda.dathost.net) -- nao com o pack Deadheimmods/Deadheim do "
            "Thunderstore. Rode installer/generate-manifest.py --check para ver "
            "onde o pack publicado diverge deste manifest, so como aviso; ele "
            "nao sobrescreve mais o arquivo."
        ),
        "packVersion": pack_version,
        "ownMods": own_mods,
        "thunderstoreMods": thunderstore_mods,
    }

    texto = json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"
    destinos = [out_path, MANIFEST_PATH]
    for caminho in destinos:
        with open(caminho, "w", encoding="utf-8") as f:
            f.write(texto)

    required = sum(1 for m in thunderstore_mods if m["required"])
    optional = len(thunderstore_mods) - required
    print(f"Escrito {' e '.join(destinos)}")
    print(f"  {len(own_mods)} mod(s) próprio(s)")
    print(f"  {required} obrigatórios, {optional} opcionais")
    return 0


def main():
    ap = argparse.ArgumentParser()
    mode = ap.add_mutually_exclusive_group(required=True)
    mode.add_argument("--check", action="store_true",
                       help="compara o manifest atual com o pack publicado, sem escrever nada (padrão recomendado)")
    mode.add_argument("--write", action="store_true",
                       help="bootstrap: reescreve o manifest do zero a partir do pack publicado")
    ap.add_argument("--out", default="DeadheimLauncher/manifest.sample.json")
    args = ap.parse_args()

    if args.check:
        return cmd_check()
    return cmd_write(args.out)


if __name__ == "__main__":
    sys.exit(main())
