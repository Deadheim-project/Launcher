#!/usr/bin/env python3
"""
Monta o rankings.json que o launcher mostra na aba RANKING.

Os dados vivem no servidor, em arquivos que os mods gravam:

  BepInEx/config/Deadheim/pvp-<mundo>.txt   K/D, PK e bounties (Deadheim, PvpStoreFormat)
  BepInEx/config/RaidSystem/RaidData.json   pontos de guerra e castelos (RaidSystem)
  BepInEx/config/Detalhes.RaidSystem.cfg    quanto vale cada abate, conquista, defesa e morte

O workflow rankings.yml baixa esses arquivos por FTP e chama este script. Ele
nao fala com rede nenhuma: recebe os arquivos e escreve o JSON, entao da para
rodar e testar com copias locais.

O que sai e so o que ja e publico dentro do jogo (nome, abates, mortes, pote da
bounty). Nada de id de jogador, moedas pendentes, quem pagou a bounty ou tempo
restante de PK.

Uso:
  python gerar_rankings.py --pvp pvp-mundo.txt --raid RaidData.json \
      [--raid-cfg Detalhes.RaidSystem.cfg] --saida rankings.json
"""

import argparse
import datetime
import json
import os
import re
import sys

VERSAO_FORMATO = 1

# Limites do que vai para o launcher: o ranking e uma vitrine, nao o banco inteiro.
MAX_GUILDAS = 30
MAX_PVP = 50
MAX_CACADOS = 30

# Padroes do RaidSystem (RaidSystemPlugin.cs, secao "4 - Scoring"), usados quando
# o .cfg do servidor nao veio ou nao tem a chave.
PONTOS_PADRAO = {
    "Points Per Kill": 10,
    "Points Per Conquest": 50,
    "Points Per Defense": 50,
    "Points Lost Per Death": 3,
}


# ---------------------------------------------------------------- leitura

def ler_pvp(texto):
    """Le o formato do PvpStoreFormat: uma linha por registro, chave=valor separados por TAB."""
    jogadores, bounties = [], []
    for bruta in (texto or "").split("\n"):
        linha = bruta.rstrip("\r")
        if not linha.strip() or linha.lstrip().startswith("#"):
            continue
        partes = linha.split("\t")
        campos = {}
        for parte in partes[1:]:
            if "=" in parte:
                chave, valor = parte.split("=", 1)
                campos[chave.strip().lower()] = valor
        tipo = partes[0].strip().lower()
        if tipo == "player":
            jogadores.append(campos)
        elif tipo == "bounty":
            bounties.append(campos)
    return jogadores, bounties


def ler_raid(texto):
    if not texto or not texto.strip():
        return {"players": [], "scores": [], "territories": []}
    # O RaidSystem grava com Newtonsoft; pode vir com BOM.
    dados = json.loads(texto.lstrip("﻿"))
    return {
        "players": dados.get("players") or [],
        "scores": dados.get("scores") or [],
        "territories": dados.get("territories") or [],
    }


def ler_pontos(texto):
    pontos = dict(PONTOS_PADRAO)
    for linha in (texto or "").splitlines():
        m = re.match(r"\s*([^#=\[][^=]*?)\s*=\s*(-?\d+)\s*$", linha)
        if m and m.group(1) in pontos:
            pontos[m.group(1)] = int(m.group(2))
    return pontos


def inteiro(campos, chave):
    try:
        return int(str(campos.get(chave, "0")).strip())
    except ValueError:
        return 0


def real(campos, chave):
    try:
        return float(str(campos.get(chave, "0")).strip())
    except ValueError:
        return 0.0


def verdadeiro(campos, chave):
    valor = str(campos.get(chave, "")).strip().lower()
    return valor in ("1", "true")


def num(valor):
    try:
        return int(valor or 0)
    except (TypeError, ValueError):
        return 0


# ---------------------------------------------------------------- montagem

def montar(pvp_texto, raid_texto, cfg_texto, agora=None):
    jogadores, bounties = ler_pvp(pvp_texto)
    raid = ler_raid(raid_texto)
    pontos = ler_pontos(cfg_texto)

    # Guilda de cada jogador, pelo que o RaidSystem sabe (pontuacao ou cadastro).
    guilda_de = {}
    for registro in raid["players"] + raid["scores"]:
        pid, time = str(registro.get("PlayerId") or ""), registro.get("TeamId")
        if pid and time:
            guilda_de[pid] = time

    # ---- guildas: soma dos pontos dos membros, como o placar do jogo (ScoreManager.GetTeamRanking)
    guildas = {}
    for s in raid["scores"]:
        time = (s.get("TeamId") or "").strip()
        if not time:
            continue
        g = guildas.setdefault(time.lower(), {
            "name": time, "points": 0, "members": 0,
            "kills": 0, "deaths": 0, "conquests": 0, "defenses": 0, "castles": [],
        })
        k, d, c, df = num(s.get("Kills")), num(s.get("Deaths")), num(s.get("Conquests")), num(s.get("Defenses"))
        g["points"] += (k * pontos["Points Per Kill"] + c * pontos["Points Per Conquest"]
                        + df * pontos["Points Per Defense"] - d * pontos["Points Lost Per Death"])
        g["members"] += 1
        g["kills"] += k
        g["deaths"] += d
        g["conquests"] += c
        g["defenses"] += df

    for t in raid["territories"]:
        dono = (t.get("OwnerTeamId") or "").strip()
        nome = (t.get("Name") or "").strip()
        if not dono or not nome:
            continue
        g = guildas.setdefault(dono.lower(), {
            "name": dono, "points": 0, "members": 0,
            "kills": 0, "deaths": 0, "conquests": 0, "defenses": 0, "castles": [],
        })
        g["castles"].append(nome)

    # Um nome so por guilda: o RaidSystem pode ter gravado "lobos" e "Lobos".
    for pid, time in list(guilda_de.items()):
        if time.lower() in guildas:
            guilda_de[pid] = guildas[time.lower()]["name"]

    lista_guildas = sorted(guildas.values(), key=lambda g: (-g["points"], -len(g["castles"]), g["name"].lower()))
    lista_guildas = lista_guildas[:MAX_GUILDAS]
    for g in lista_guildas:
        g["castles"].sort(key=str.lower)

    # ---- PvP: abates contra jogador, do Deadheim. Quem nunca lutou nao entra.
    pvp = []
    for j in jogadores:
        kills, deaths = inteiro(j, "kills"), inteiro(j, "deaths")
        if kills <= 0 and deaths <= 0:
            continue
        pid = str(j.get("id", "")).strip()
        pvp.append({
            "name": (j.get("name") or "").strip() or "?",
            "guild": guilda_de.get(pid),
            "kills": kills,
            "deaths": deaths,
            "pkKills": inteiro(j, "pkkills"),
            "isPk": verdadeiro(j, "pkpermanent") or real(j, "pkleft") > 0,
            "pve": verdadeiro(j, "pvepermanent"),
        })
    pvp.sort(key=lambda p: (-p["kills"], p["deaths"], p["name"].lower()))
    pvp = pvp[:MAX_PVP]

    # ---- cacados: as bounties abertas. delayLeft > 0 e o aviso antes da caca comecar.
    cacados = []
    for b in bounties:
        pid = str(b.get("target", "")).strip()
        cacados.append({
            "name": (b.get("name") or "").strip() or "?",
            "guild": guilda_de.get(pid),
            "pot": inteiro(b, "pot"),
            "hunted": real(b, "delayleft") <= 0,
        })
    cacados.sort(key=lambda c: (not c["hunted"], -c["pot"], c["name"].lower()))
    cacados = cacados[:MAX_CACADOS]

    agora = agora or datetime.datetime.now(datetime.timezone.utc)
    return {
        "version": VERSAO_FORMATO,
        "generatedAt": agora.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "guilds": lista_guildas,
        "hunted": cacados,
        "pvp": pvp,
    }


def ler_arquivo(caminho):
    if not caminho or not os.path.isfile(caminho):
        return ""
    with open(caminho, encoding="utf-8", errors="replace") as f:
        return f.read()


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--pvp", help="pvp-<mundo>.txt do Deadheim")
    p.add_argument("--raid", help="RaidData.json do RaidSystem")
    p.add_argument("--raid-cfg", help="Detalhes.RaidSystem.cfg (pontuacao)")
    p.add_argument("--saida", required=True, help="onde gravar o rankings.json")
    args = p.parse_args(argv)

    pvp_texto, raid_texto = ler_arquivo(args.pvp), ler_arquivo(args.raid)
    if not pvp_texto and not raid_texto:
        # Sem nenhum dos dois, publicar um ranking vazio apagaria o que esta no ar.
        print("nenhum arquivo de dados encontrado; nada publicado", file=sys.stderr)
        return 1

    dados = montar(pvp_texto, raid_texto, ler_arquivo(args.raid_cfg))
    with open(args.saida, "w", encoding="utf-8") as f:
        json.dump(dados, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print(f"{len(dados['guilds'])} guilda(s), {len(dados['hunted'])} cacado(s), {len(dados['pvp'])} jogador(es) no PvP")
    return 0


if __name__ == "__main__":
    sys.exit(main())
