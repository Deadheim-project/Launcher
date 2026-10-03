namespace DeadheimLauncher.Models;

/// <summary>
/// Placar do servidor mostrado na aba RANKING: guildas, jogadores caçados e PvP.
///
/// Não sai do manifest: os números mudam a cada abate, e o manifest só muda
/// quando alguém publica. Quem gera é o workflow rankings.yml, que de tempos em
/// tempos lê os arquivos que o Deadheim e o RaidSystem gravam no servidor
/// (tools/rankings/gerar_rankings.py) e publica o rankings.json no branch
/// "rankings" deste repositório.
/// </summary>
public sealed class Rankings
{
    public int Version { get; set; }

    /// <summary>Quando o servidor foi lido, em UTC.</summary>
    public DateTimeOffset? GeneratedAt { get; set; }

    public List<GuildRank> Guilds { get; set; } = new();
    public List<HuntedPlayer> Hunted { get; set; } = new();
    public List<PvpRank> Pvp { get; set; } = new();
}

/// <summary>Uma guilda no Ranking de Guerra: a soma dos pontos dos membros, como no placar do jogo.</summary>
public sealed class GuildRank
{
    public int Position { get; set; }
    public string Name { get; set; } = "";
    public int Points { get; set; }
    public int Members { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Conquests { get; set; }
    public int Defenses { get; set; }

    /// <summary>Castelos que a guilda segura agora.</summary>
    public List<string> Castles { get; set; } = new();

    public bool TemCastelos => Castles.Count > 0;
    public string CastelosTexto => string.Join(", ", Castles);

    public string Resumo =>
        $"{Members} {(Members == 1 ? "membro" : "membros")} · {Kills} abates · {Deaths} mortes · " +
        $"{Conquests} {(Conquests == 1 ? "conquista" : "conquistas")}";
}

/// <summary>Uma cabeça a prêmio (/bounty).</summary>
public sealed class HuntedPlayer
{
    public int Position { get; set; }
    public string Name { get; set; } = "";
    public string? Guild { get; set; }
    public int Pot { get; set; }

    /// <summary>Já está sendo caçado. Falso é o aviso antes de a caçada começar.</summary>
    public bool Hunted { get; set; }

    public bool TemGuilda => !string.IsNullOrWhiteSpace(Guild);
    public string Situacao => Hunted ? "CAÇADO" : "EM BREVE";
    public string PoteTexto => $"{Pot:N0} moedas";
}

/// <summary>Um jogador no ranking de PvP (abates contra jogador, fora da arena).</summary>
public sealed class PvpRank
{
    public int Position { get; set; }
    public string Name { get; set; } = "";
    public string? Guild { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int PkKills { get; set; }
    public bool IsPk { get; set; }

    /// <summary>Escolheu o PvE permanente: o histórico fica, mas ele não luta mais.</summary>
    public bool Pve { get; set; }

    public bool TemGuilda => !string.IsNullOrWhiteSpace(Guild);
    public string Kd => Deaths <= 0 ? Kills.ToString("0.00") : ((double)Kills / Deaths).ToString("0.00");
}
