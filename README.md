<div align="center">

# SoNexus

**O seu PC inteiro num app só.**
Hardware, jogos, desempenho, otimização e iluminação para Windows — grátis e de código aberto.

por **SoStudio** · criado por **SoEuzinho**

[**⬇ Baixar a última versão**](https://github.com/SoEuzinho/SoNexus/releases/latest) ·
[🎬 Vídeo de divulgação](docs/media/divulgacao.mp4) ·
[▶ Demonstração completa](docs/media/demo.mp4)

<img src="docs/media/demo.gif" alt="Demonstração do SoNexus passando pelas telas" width="860">

</div>

---

## O que ele faz

Em vez de ter CPU-Z, HWMonitor, MSI Afterburner, Process Lasso, RAMMap, Ninite e um monte de scripts, o SoNexus junta tudo numa interface só, em português, com explicação simples do que cada número significa e do que fazer.

| | |
|---|---|
| **Hardware por peça** | Processador, placa de vídeo, RAM, discos, placa-mãe e fonte, cada um com informações, sensores (com mínimo e máximo), testes e dicas. |
| **Saúde do PC** | Acha sozinho o que está segurando o computador: RAM em canal único, EXPO/XMP desligado, disco cheio, BIOS antiga, monitor abaixo do Hz. |
| **Jogos** | Biblioteca de todos os launchers, FPS ao vivo, 1% low, engasgos e **gargalo medido de verdade durante a partida**. |
| **Painel sobre o jogo** | Overlay com temas animados, configurável e leve (menos de 0,25% de CPU). |
| **Desempenho** | Teste completo (CPU, GPU, VRAM, RAM e disco, 3 rodadas), comparação antes/depois, stress test com corte por temperatura, overclock da placa e guia de EXPO, PBO e BIOS. |
| **Ferramentas** | Liberar memória em espera, prioridade automática para jogos, limpeza de temporários, instalar vários programas de uma vez e teste de monitor (Hz e rastro). |
| **Otimização** | Mais de 70 ajustes do Windows (telemetria, anúncios, IA, jogos), todos com backup e botão de desfazer. |
| **Drivers, rede e áudio** | Driver de vídeo com um clique, remoção limpa (estilo DDU), rotas até servidores de jogos, DNS, Voicemeeter. |
| **Iluminação** | RGB pelo OpenRGB e fitas de LED Bluetooth (BJ_LED), com efeitos que seguem a temperatura, o uso ou a música. |

## Telas

### Saúde do PC e hardware por peça
<img src="docs/media/hardware.png" alt="Visão geral do hardware com a saúde do PC" width="860">

### Gargalo e FPS ao vivo
<img src="docs/media/gargalo.png" alt="Gargalo medido nas partidas e estimado por resolução" width="860">
<img src="docs/media/fps-ao-vivo.png" alt="FPS ao vivo com a prévia do painel sobre o jogo" width="860">

### Temas do painel sobre o jogo
<img src="docs/media/temas.gif" alt="Temas animados do painel: Neon, Imperial, Pixel, Arena, Fofo e Cristal" width="860">

<sub>Temas originais do SoNexus (valores ilustrativos na animação). Nenhum usa marca, logo ou personagem de jogo ou empresa.</sub>

### Desempenho: EXPO, PBO e BIOS
<img src="docs/media/expo-pbo-bios.png" alt="Guia de EXPO, PBO e BIOS com dados do PC" width="860">

### Ferramentas
<img src="docs/media/instalar-programas.png" alt="Instalar vários programas de uma vez" width="860">
<img src="docs/media/limpeza.png" alt="Limpeza de temporários e cache de shaders" width="860">

### Iluminação RGB e otimização do Windows
<img src="docs/media/rgb.png" alt="Iluminação RGB pelo OpenRGB e fitas Bluetooth" width="860">
<img src="docs/media/otimizacao.png" alt="Debloat do Windows com ajustes reversíveis" width="860">

## Vídeos

- 🎬 **[Vídeo de divulgação (40 s)](docs/media/divulgacao.mp4)**: o SoNexus em 40 segundos, para compartilhar.
- ▶ **[Demonstração completa (47 s)](docs/media/demo.mp4)**: passando pelas telas do app.

## Instalar

1. Baixe o `SoNexus-Setup-<versão>.exe` na página **[Releases](https://github.com/SoEuzinho/SoNexus/releases/latest)**.
2. (Opcional) confira o arquivo com o SHA-256 publicado ao lado:
   ```powershell
   Get-FileHash .\SoNexus-Setup-0.25.0.exe -Algorithm SHA256
   ```
3. Instale e abra. Na primeira vez, o SoNexus mostra os Termos de Uso e Privacidade.

Requisitos: Windows 10 (1809+) ou 11, 64 bits. Não precisa instalar o .NET. O app pede administrador (sensores, ventoinhas e ajustes do sistema).

Para a temperatura do processador, o SoNexus oferece instalar o driver **PawnIO** (assinado), que substitui o antigo WinRing0 bloqueado pelo Windows Defender.

## Segurança e privacidade

- **Nada sai do seu PC**: sem conta, sem propaganda, sem rastreamento. Consultas na internet (requisitos de jogos, drivers) podem ser desligadas.
- **Tudo tem desfazer**: cada ajuste do Windows guarda o valor original.
- **Jogos e anti-cheat**: o FPS é medido pelos eventos públicos do Windows (ETW, o mesmo método do PresentMon) e o painel é uma janela separada. O SoNexus não lê nem altera a memória dos jogos. Mesmo assim, cada anti-cheat tem regras próprias: em jogos competitivos, se tiver dúvida, desligue o painel.
- **Overclock com travas**: tensão bloqueada, limites de temperatura e regras de segurança antes de liberar os controles.

## Compilar

```powershell
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup
.\build.ps1
```

Gera `publish\<versão>\` (versão portátil) e `installer\Output\SoNexus-Setup-<versão>.exe` + `.sha256`.

Feito em C# / WPF (.NET 8) com LibreHardwareMonitor, TraceEvent (ETW), NAudio, Vortice (Direct3D 11) e OpenRGB SDK.

## Licença e créditos

Código sob a licença [MIT](LICENSE) — © 2026 SoEuzinho (SoStudio).
Bibliotecas e projetos de terceiros: veja [THIRD-PARTY-NOTICES.txt](SoNexus/THIRD-PARTY-NOTICES.txt).
Marcas citadas (NVIDIA, AMD, Intel, Microsoft, Steam etc.) pertencem aos seus donos; o SoNexus não é afiliado a elas.
