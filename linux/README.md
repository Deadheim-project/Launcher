# Deadheim no Linux (Valheim nativo)

Script único, sem Wine, sem Proton e sem .NET. Só precisa de `python3` e da Steam aberta.

```bash
python3 deadheim.py config --valheim "/caminho/steamapps/common/Valheim" --senha SENHA
python3 deadheim.py opcionais     # escolhe os mods opcionais
python3 deadheim.py jogar         # atualiza os mods e abre o jogo
```

Outros comandos: `instalar`, `log`, `limpar`. Detalhes em `python3 deadheim.py --help`.
O Valheim precisa ser a versão nativa (`valheim.x86_64`), sem Proton forçado na Steam.
