# SoNexus

Kit de ferramentas para PC Windows, por **SoStudio** — criado por **SoEuzinho**.

Tudo num app só, com interface minimalista preto e branco:

- **Hardware**: peças, sensores (temperatura, consumo, tensões), memória (latência e banda), saúde dos discos (SMART + varredura de setores), energia e custo na conta de luz.
- **Otimização**: debloat do Windows (ajustes reversíveis com backup, remoção de apps), inicialização estilo Autoruns, itens ocultos.
- **Drivers**: instalar driver NVIDIA/AMD/Intel com um clique, drivers de todos os dispositivos, remoção limpa do driver de vídeo (estilo DDU).
- **Jogos**: biblioteca de todos os launchers com FPS estimado, gargalo, FPS ao vivo com painel sobre o jogo, overclock e ventoinhas (com regras de segurança), benchmarks e stress test de CPU, GPU, VRAM, RAM e disco, realidade virtual.
- **Rede**: visão geral, apps e conexões, rotas até servidores de jogos (estilo WinMTR), segurança, DNS.
- **Áudio**: dispositivos, Voicemeeter, drivers.
- **Diagnóstico**: telas azuis e erros, teste de RAM, latência DPC.
- **Integração**: RGB via OpenRGB, iniciar com o Windows, segundo plano na bandeja.

## Instalar

Baixe o `SoNexus-Setup-<versão>.exe` na página **Releases** e confira o SHA-256 publicado ao lado:

```powershell
Get-FileHash .\SoNexus-Setup-0.13.0.exe -Algorithm SHA256
```

Requisitos: Windows 10 (1809+) ou 11, 64 bits. Não precisa instalar o .NET. O app pede administrador (sensores, ventoinhas e ajustes do sistema).

Para a temperatura do processador, o SoNexus oferece instalar o driver **PawnIO** (assinado). Ele substitui o antigo WinRing0, que o Windows Defender bloqueia.

## Compilar

```powershell
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup
.\build.ps1
```

Gera `publish\<versão>\` (versão portátil) e `installer\Output\SoNexus-Setup-<versão>.exe` + `.sha256`.

## Licença e créditos

Código sob a licença [MIT](LICENSE) — © 2026 SoEuzinho (SoStudio).
Bibliotecas e projetos de terceiros: veja [THIRD-PARTY-NOTICES.txt](SoNexus/THIRD-PARTY-NOTICES.txt).
