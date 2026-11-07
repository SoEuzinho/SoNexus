# Vídeo de divulgação

Monta `docs/media/divulgacao.mp4`: narração com a voz do Windows (Microsoft Maria, pt-BR), cenas animadas desenhadas em WPF quadro a quadro na paleta do app, timeline de capítulos e um fundo sonoro gerado na hora (sem música de terceiros).

Precisa do ffmpeg (`winget install yt-dlp.FFmpeg` ou `Gyan.FFmpeg`) e dos prints em `docs/media`.

```powershell
dotnet run -c Release -- <pasta do repositório> <caminho do ffmpeg.exe>
```

Para mudar o texto falado, as telas ou os tópicos, edite a lista `scenes` no começo do `Program.cs`.
