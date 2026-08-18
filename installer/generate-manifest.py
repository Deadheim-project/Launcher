#!/usr/bin/env python3
"""
Gera o manifest.json do launcher a partir do modpack Deadheim publicado no
Thunderstore.

Por que gerar em vez de manter na mão: o pack tem ~40 mods com versão fixada.
Manter isso manualmente sincronizado com o servidor é erro na certa — e mod em
versão diferente da do servidor é causa clássica de desync e de crash ao
entrar. Aqui a fonte da verdade é o próprio pack: publicou versão nova do
Deadheim, roda isso de novo e commita o resultado.

Uso:
    python installer/generate-manifest.py
    python installer/generate-manifest.py --out DeadheimLauncher/manifest.sample.json
"""

import argparse
import json
import sys
import urllib.request

PACK_NAMESPACE = "Deadheimmods"
PACK_NAME = "Deadheim"
API = "https://thunderstore.io/api/experimental/package/{ns}/{name}/"
UA = {"User-Agent": "DeadheimLauncher-manifest-generator"}

# O pacote do BepInEx não é um plugin: ele é o carregador, e vai na raiz da
# pasta do Valheim (winhttp.dll ao lado do valheim.exe).
GAME_ROOT_PACKAGES = {"BepInExPack_Valheim"}

# Mods que ainda constam do pack publicado mas sairam do servidor. Ficam listados
# aqui, e nao removidos silenciosamente, porque o pack no Thunderstore continua
# declarando a dependencia: sem esta lista, a proxima geracao traria o mod de volta.
#
# Marketplace_And_Server_NPCs_Revamped: substituido pelo mod de NPCs proprio
# (Deadheim-project/npcs). Removido do servidor em 18/08/2026; o conteudo dele --
# 248 quests, 54 mercadores, 59 quadros de missao, 2 redes de teleporte -- foi
# importado e agora acompanha o nosso mod.
EXCLUDED_PACKAGES = {"Marketplace_And_Server_NPCs_Revamped"}

# Mods de conveniência/admin que rodam só no cliente: podem ficar de fora sem
# quebrar a entrada no servidor, então entram como opcionais.
OPTIONAL_PACKAGES = {
    "Server_devcommands",
    "DevToggle",
    "Azus_UnOfficial_ConfigManager",
}

# Mods de autoria própria, que não vêm do Thunderstore e sim de GitHub Releases.
#
# assetPattern ".zip" casa com qualquer zip do release: são repositórios de um
# mod só, e assim o nome exato do arquivo não precisa ser combinado de antemão.
# Se um release passar a ter vários zips, troque pelo nome exato.
OWN_MODS_OWNER = "Deadheim-project"
OWN_MODS_AUTHOR = "Detalhes"
OWN_MODS = [
    ("npcs", "NPCs", "Mercador, Teleportador, Correio e Missões."),
    ("Deadheim", "Deadheim", "Mod base do servidor."),
    ("RaidSystem", "Raid System", "Sistema de raides."),
    ("Hearthstone", "Hearthstone", "Pedra de retorno."),
    ("donationshop", "Donation Shop", "Loja de doações."),
]

# Ferramentas de administração. Ficam numa aba separada para não atrapalhar
# quem só quer entrar e jogar, e nenhuma é obrigatória.
#
# Os nomes foram conferidos um a um contra a API do Thunderstore: namespace
# errado vira 404 na máquina de quem clicar.
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

# Melhorias que qualquer jogador pode querer, e que não são ferramenta de
# administração. Nenhuma é exigida pelo servidor.
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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="DeadheimLauncher/manifest.sample.json")
    args = ap.parse_args()

    pack = fetch(API.format(ns=PACK_NAMESPACE, name=PACK_NAME))
    latest = pack["latest"]
    pack_version = latest["version_number"]
    deps = latest["dependencies"]

    print(f"Pack {PACK_NAMESPACE}/{PACK_NAME} v{pack_version} -> {len(deps)} dependências")

    thunderstore_mods = []
    for dep in deps:
        ns, name, version = parse_dependency(dep)
        if name in EXCLUDED_PACKAGES:
            print(f"  ignorando {ns}/{name}: nao roda mais no servidor")
            continue
        entry = {
            "id": slugify(name),
            "name": name.replace("_", " "),
            "description": f"Do modpack Deadheim {pack_version}.",
            "required": name not in OPTIONAL_PACKAGES,
            "source": "Thunderstore",
            "thunderstoreNamespace": ns,
            "thunderstoreName": name,
            "version": version,
            # No Thunderstore o namespace é o autor, e a URL da página é
            # derivável dele. Crédito visível e clicável no launcher é o mínimo
            # devido a quem fez os mods que o servidor usa.
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
            "id": slugify(name),
            "name": name.replace("_", " "),
            "description": desc,
            # Nenhuma ferramenta de admin é obrigatória, e sem versão fixada
            # porque não precisam casar com o servidor como os mods do pack.
            "required": False,
            "category": "Admin",
            "source": "Thunderstore",
            "thunderstoreNamespace": ns,
            "thunderstoreName": name,
            "author": ns,
            "url": f"https://thunderstore.io/c/valheim/p/{ns}/{name}/",
        })

    for ns, name, desc in OPTIONAL_MODS:
        if any(m["thunderstoreName"] == name for m in thunderstore_mods):
            continue
        thunderstore_mods.append({
            "id": slugify(name),
            "name": name.replace("_", " "),
            "description": desc,
            "required": False,
            "category": "Opcional",
            "source": "Thunderstore",
            "thunderstoreNamespace": ns,
            "thunderstoreName": name,
            "author": ns,
            "url": f"https://thunderstore.io/c/valheim/p/{ns}/{name}/",
        })

    own_mods = [
        {
            "id": slugify(repo),
            "name": nome,
            "description": desc,
            "required": True,
            "source": "GitHub",
            "gitHubOwner": OWN_MODS_OWNER,
            "gitHubRepo": repo,
            "assetPattern": ".zip",
            "author": OWN_MODS_AUTHOR,
            # Página de releases, e não a raiz do repositório: é de onde a DLL
            # sai, e o link continua válido a cada versão nova.
            "url": f"https://github.com/{OWN_MODS_OWNER}/{repo}/releases",
        }
        for repo, nome, desc in OWN_MODS
    ]

    manifest = {
        "_comment": (
            f"Gerado por installer/generate-manifest.py a partir de "
            f"{PACK_NAMESPACE}/{PACK_NAME} v{pack_version}. Não edite à mão: "
            f"publique a nova versão do pack e rode o script de novo."
        ),
        "packVersion": pack_version,
        "ownMods": own_mods,
        "thunderstoreMods": thunderstore_mods,
    }

    texto = json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"

    # Escreve os dois de uma vez, de propósito:
    #   - manifest.sample.json vai embutido no launcher, como reserva offline
    #   - manifest.json na raiz é o que os jogadores realmente baixam
    # Já aconteceu de eu atualizar só o sample e o launcher continuar servindo
    # a versão antiga pelo raw.githubusercontent, sem nada indicar o problema.
    destinos = [args.out, "manifest.json"]
    for caminho in destinos:
        with open(caminho, "w", encoding="utf-8") as f:
            f.write(texto)

    required = sum(1 for m in thunderstore_mods if m["required"])
    optional = len(thunderstore_mods) - required
    print(f"Escrito {' e '.join(destinos)}")
    print(f"  {len(own_mods)} mod(s) próprio(s)")
    print(f"  {required} obrigatórios, {optional} opcionais")
    return 0


if __name__ == "__main__":
    sys.exit(main())
