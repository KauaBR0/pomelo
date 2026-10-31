# Pomelo

Conversor de arquivos offline para Windows, sem cliques: comece a arrastar uma imagem, música ou vídeo no Explorer, toque **Shift** e solte sobre o formato desejado. A cópia convertida aparece ao lado do original. Tudo roda localmente; nenhum arquivo sai do seu computador.

Inspirado no [Tangerine](https://tangerineformac.com/), que existe só para Mac. Projeto independente, sem relação com o Tangerine.

> **Estado:** em desenvolvimento. Converte imagens, áudio e vídeo; PDF e documentos ainda não.

## Como usar

1. Abra o Pomelo. Ele fica na bandeja do sistema (perto do relógio; na primeira vez pode estar em "Mostrar ícones ocultos").
2. No Explorer ou na Área de Trabalho, **comece a arrastar** um ou mais arquivos de imagem, áudio ou vídeo.
3. Com o botão do mouse ainda segurado, **toque Shift** (pode soltar a tecla). A roda abre sob o cursor.
4. **Solte** os arquivos sobre o formato. Soltar no centro, ou apertar **Esc**, cancela.

O original nunca é alterado nem movido, e nenhum arquivo existente é substituído: se `foto.jpg` já existir, o resultado vira `foto (1).jpg`. Ao terminar, uma notificação mostra o resultado; clicar nela abre o Explorer com o arquivo selecionado.

## Formatos

### Imagens

| | Formatos |
|---|---|
| **Saída** | JPG, PNG, TIFF, BMP, GIF |
| **Entrada** | JPG, PNG, TIFF, BMP, GIF, ICO, JPEG XR |
| **Entrada, com extensão da Microsoft Store** | WebP, HEIC/HEIF, AVIF |

- Fotos de celular são giradas conforme a orientação EXIF, então não saem deitadas.
- Em JPG, BMP e GIF (sem transparência), áreas transparentes viram fundo branco. GIF usa uma paleta otimizada de 256 cores.
- O perfil de cor é preservado em JPG, PNG e TIFF; em BMP e GIF, a imagem é convertida para sRGB.
- GIF animado e TIFF com várias páginas ainda não são suportados: o Pomelo recusa com uma mensagem, em vez de descartar quadros em silêncio.

### Áudio e vídeo (requer FFmpeg)

| | Formatos |
|---|---|
| **Áudio → áudio** | MP3, M4A (AAC), WAV, FLAC, OGG (Vorbis), OPUS, AIFF, WMA |
| **Vídeo → vídeo** | MP4 e MOV (H.264 + AAC), MKV, WEBM (VP9 + Opus), AVI, WMV, GIF animado |
| **Vídeo → áudio** | MP3 (extrai a trilha sonora) |
| **Entrada de áudio** | MP3, M4A, AAC, WAV, FLAC, OGG, OPUS, AIFF, WMA |
| **Entrada de vídeo** | MP4, M4V, MOV, MKV, WEBM, AVI, WMV, FLV, MPG, TS/MTS/M2TS, 3GP, OGV |

- Usa o primeiro fluxo de vídeo e o primeiro de áudio; legendas e capas embutidas não são copiadas.
- Vídeos com largura ou altura ímpar são ajustados em 1 pixel (exigência do H.264). GIF animado sai com 12 fps e no máximo 640 px de largura.
- Só aparecem os formatos que o FFmpeg instalado consegue gerar.
- O FFmpeg roda com prioridade baixa, para não travar o computador. Durante a conversão, passe o mouse no ícone da bandeja para ver o andamento. Enquanto converte, existe um arquivo temporário `~pomelo-….part` na pasta de saída, apagado em caso de falha ou ao sair do Pomelo.

A roda só oferece formatos que servem para **todos** os arquivos arrastados. Por exemplo, um WAV e um MP4 juntos só podem virar MP3.

### FFmpeg

O Pomelo não embute o FFmpeg nem baixa nada sozinho. Ele procura, nesta ordem: o caminho escolhido nas configurações, uma pasta `ffmpeg\` ao lado do `Pomelo.exe`, o `PATH`, e as instalações do winget, Scoop e Chocolatey. Sem FFmpeg, a roda mostra "Requer FFmpeg" para áudio e vídeo.

Para instalar, use **Instalar com winget** na janela do Pomelo, ou rode:

```sh
winget install --id Gyan.FFmpeg.Essentials --exact
```

## Configurações

Clique no ícone da bandeja para abrir a janela:

- **Roda:** ligar/desligar, e a tecla que a abre durante o arraste (Shift, Ctrl ou Alt).
- **Arquivos convertidos:** ao lado do original, em Downloads, na Área de Trabalho ou numa pasta escolhida. Qualidade do JPEG.
- **Áudio e vídeo:** qual FFmpeg está em uso, escolher outro `ffmpeg.exe`, instalar com winget ou procurar de novo.
- **Sistema:** iniciar com o Windows (abre direto na bandeja) e notificações ao concluir.

As configurações ficam em `%APPDATA%\Pomelo\settings.json`. Iniciar com o Windows usa a chave `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, sem precisar de administrador. Se você desativar o Pomelo em Gerenciador de Tarefas → Aplicativos de inicialização, a janela mostra a opção desmarcada.

Fechar a janela mantém o Pomelo na bandeja. Para encerrar: clique com o botão direito no ícone → **Sair**. Abrir o Pomelo de novo enquanto ele roda só traz a janela existente.

## Compilar e rodar

Requisitos: Windows 10 ou 11 (x64) e o [SDK do .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
git clone https://github.com/KauaBR0/pomelo.git
cd pomelo
dotnet run --project src/Pomelo
```

Para iniciar já na bandeja, sem a janela: `dotnet run --project src/Pomelo -- --background`.

Testes:

```sh
dotnet test
```

Os testes de áudio e vídeo rodam o FFmpeg de verdade e são pulados se ele não for encontrado.

### Desinstalar

Desmarque "Iniciar com o Windows" (ou apague o valor `Pomelo` na chave `Run`), saia pela bandeja e apague a pasta `%APPDATA%\Pomelo`.

## Como funciona

O macOS tem uma área de transferência global do arraste, então um app consegue saber o que está sendo arrastado em qualquer lugar. O Windows não tem. O Pomelo resolve assim:

```mermaid
sequenceDiagram
    participant U as Usuário
    participant H as Hooks globais (thread própria)
    participant W as Roda (janela overlay = alvo OLE)
    participant C as Conversor (thread STA)
    U->>H: arrasta + toca Shift
    H->>W: abrir sob o cursor (invisível, sem roubar o foco)
    U->>W: cursor se move sobre a roda
    W->>W: DragEnter recebe a lista de arquivos (CF_HDROP) → desenha os formatos
    U->>W: solta em "JPG"
    W-->>U: efeito Copy (nunca Move)
    W->>C: converter em segundo plano
```

- **Detecção do gesto** (`src/Pomelo/Input/GestureMonitor.cs`): hooks de mouse e teclado de baixo nível numa thread dedicada. Só observam, nunca consomem ou registram teclas.
- **Roda** (`src/Pomelo/Wheel/`): janela sempre no topo, que não ativa, registrada como alvo de soltura. Ela abre invisível e só aparece se um arraste de *arquivos* entrar nela. Arrastes de texto ou links passam direto.
- **Nunca move o original:** no Explorer, Shift durante o arraste significa "mover". A roda sempre responde "copiar".
- **Conversão** (`src/Pomelo.Core/Conversion/`): imagens pelo Windows Imaging Component (WIC, via WPF), sem dependências externas; áudio e vídeo por um processo FFmpeg (`src/Pomelo.Core/Media/`). Cada arquivo de um lote falha ou passa de forma independente.

### Estrutura

```
src/Pomelo/          app WPF: bandeja, roda, hooks, janela de configurações
src/Pomelo.Core/     formatos, conversor, configurações, inicialização com o Windows
tests/Pomelo.Tests/  testes do núcleo (xUnit)
```

## Próximos passos

- Roda de ferramentas (comprimir, redimensionar, girar…) e roda de ações de arquivo
- GIF animado como entrada (por exemplo, GIF → MP4)
- PDF e documentos
- Instalador

## Licença

[MIT](LICENSE)
