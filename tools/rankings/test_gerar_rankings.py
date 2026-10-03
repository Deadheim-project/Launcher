"""Testes do gerar_rankings.py: python -m unittest discover tools/rankings"""

import datetime
import json
import unittest

import gerar_rankings as g

PVP = (
    "# Deadheim PvP\n"
    "version\t3\n"
    "player\tid=1\tname=Ragnar\tkills=12\tdeaths=3\tpkLeft=0\tpkPermanent=0\tpkKills=4\tpendingCoins=777\n"
    "player\tid=2\tname=Bjorn\tkills=12\tdeaths=1\tpkLeft=120.5\tpkKills=1\n"
    "player\tid=3\tname=Calmo\tkills=0\tdeaths=0\tpvePermanent=1\n"
    "player\tid=4\tname=Lagertha\tkills=2\tdeaths=7\tpkPermanent=1\n"
    "bounty\ttarget=2\tname=Bjorn\tpot=5000\tdelayLeft=0\telapsed=30\n"
    "contrib\ttarget=2\tid=1\tname=Ragnar\tamount=5000\n"
    "bounty\ttarget=4\tname=Lagertha\tpot=9000\tdelayLeft=45\telapsed=0\n"
    "lixo sem formato\n"
)

RAID = json.dumps({
    "players": [{"PlayerId": "4", "Nick": "Lagertha", "TeamId": "Corvos"}],
    "scores": [
        {"PlayerId": "1", "Nick": "Ragnar", "TeamId": "Lobos", "Kills": 5, "Deaths": 2, "Conquests": 1, "Defenses": 0},
        {"PlayerId": "2", "Nick": "Bjorn", "TeamId": "lobos", "Kills": 1, "Deaths": 0, "Conquests": 0, "Defenses": 1},
        {"PlayerId": "9", "Nick": "Sem", "TeamId": "", "Kills": 99},
    ],
    "territories": [
        {"Name": "Fortaleza", "OwnerTeamId": "Corvos"},
        {"Name": "Abismo", "OwnerTeamId": "Lobos"},
        {"Name": "Livre", "OwnerTeamId": None},
    ],
})

CFG = "[4 - Scoring]\n## Points per enemy kill.\nPoints Per Kill = 10\nPoints Per Defense = 25\n"


class GerarRankingsTest(unittest.TestCase):
    def setUp(self):
        agora = datetime.datetime(2026, 10, 3, 12, 0, tzinfo=datetime.timezone.utc)
        self.r = g.montar(PVP, "﻿" + RAID, CFG, agora)

    def test_guildas_somam_pontos_com_a_cfg_do_servidor(self):
        lobos = self.r["guilds"][0]
        # Ragnar: 5*10 + 1*50 - 2*3 = 94; Bjorn: 1*10 + 1*25 = 35
        self.assertEqual(lobos["name"], "Lobos")
        self.assertEqual(lobos["points"], 129)
        self.assertEqual(lobos["members"], 2)
        self.assertEqual(lobos["castles"], ["Abismo"])

    def test_guilda_so_com_castelo_aparece(self):
        corvos = [x for x in self.r["guilds"] if x["name"] == "Corvos"]
        self.assertEqual(corvos[0]["castles"], ["Fortaleza"])
        self.assertEqual(corvos[0]["points"], 0)

    def test_pontuacao_sem_guilda_nao_vira_guilda(self):
        self.assertEqual(len(self.r["guilds"]), 2)

    def test_pvp_ordena_por_abates_e_desempata_por_mortes(self):
        nomes = [p["name"] for p in self.r["pvp"]]
        self.assertEqual(nomes, ["Bjorn", "Ragnar", "Lagertha"])

    def test_pvp_marca_pk_e_guilda(self):
        por_nome = {p["name"]: p for p in self.r["pvp"]}
        self.assertTrue(por_nome["Bjorn"]["isPk"])
        self.assertTrue(por_nome["Lagertha"]["isPk"])
        self.assertFalse(por_nome["Ragnar"]["isPk"])
        self.assertEqual(por_nome["Lagertha"]["guild"], "Corvos")
        self.assertEqual(por_nome["Bjorn"]["guild"], "Lobos")

    def test_nao_publica_dados_privados(self):
        texto = json.dumps(self.r)
        self.assertNotIn("pendingCoins", texto)
        self.assertNotIn("777", texto)
        self.assertNotIn("contrib", texto)
        self.assertNotIn('"id"', texto)

    def test_cacados_primeiro_os_ja_em_caca(self):
        self.assertEqual([c["name"] for c in self.r["hunted"]], ["Bjorn", "Lagertha"])
        self.assertTrue(self.r["hunted"][0]["hunted"])
        self.assertFalse(self.r["hunted"][1]["hunted"])
        self.assertEqual(self.r["hunted"][1]["pot"], 9000)

    def test_data_em_utc(self):
        self.assertEqual(self.r["generatedAt"], "2026-10-03T12:00:00Z")

    def test_arquivos_vazios_nao_quebram(self):
        r = g.montar("", "", "")
        self.assertEqual((r["guilds"], r["hunted"], r["pvp"]), ([], [], []))


if __name__ == "__main__":
    unittest.main()
