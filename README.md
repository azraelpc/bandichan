# Bandichan (beta - mvp)

Bandichan es una app de grabacion de pantalla para Windows con flujo tray-first y editor integrado para recorte, cortes por segmentos y exportacion nativa.

## Objetivo del proyecto

- Iniciar y detener grabacion desde el icono de bandeja.
- Seleccionar monitor antes de grabar (con confirmacion por doble clic).
- Abrir editor al terminar para recortar, redimensionar y eliminar segmentos.
- Exportar a MP4 con pipeline nativo de Windows (sin FFmpeg en el flujo principal).

## Estado actual

- Grabacion nativa: estable (ScreenRecorderLib + Media Foundation).
- Arranque de grabacion: robusto con reintentos de compatibilidad (audio/hardware encoding).
- Export con resize: estable.
- Export con crop: estabilizado con pipeline visual por overlays (sin CropRectangle directo).
- Audio en export con crop: conservado mediante clip base con audio + overlays de video muteados.
- Preset 8MB orientado a Discord con limite duro de 10MB (correccion automatica si se excede).
- App en bandeja: estados Idle / Recording / Processing / Editing con transiciones protegidas.
- Single-instance: habilitado (si ya existe una instancia, la segunda termina).
- Inicio con Windows: configurable desde tray (desactivado por defecto).

## Caracteristicas

### Grabacion

- Captura por monitor.
- 30 o 60 fps.
- Audio del sistema opcional (loopback).
- Seleccion de dispositivo de loopback (o auto recomendado).

### Editor

- Reproduccion, pausa y navegacion frame a frame.
- Marcas In/Out y eliminacion de segmentos.
- Undo del ultimo corte.
- Crop visual con overlay.
- Resize con lock ratio.
- Presets de export:
  - Balanced
  - Balanced+
  - 8MB H.264
  - 8MB H.265
  - Custom H.264 (MB objetivo)
  - Custom H.265 (MB objetivo)

### UX de bandeja

- Arranque en tray.
- Menu contextual: Start/Stop, Import Video, Open, Settings, Run with Windows, Tray Icon Settings..., Exit.
- Main window no bloqueante (se oculta al cerrar salvo salida explicita).
- Onboarding de visibilidad del icono: tip one-shot para abrir Taskbar settings en Windows 11.

## Arquitectura

### Proyectos

- Bandichan.App: UI WPF, tray, ventanas y flujo de usuario.
- Bandichan.Core: contratos, modelos y enums compartidos.
- Bandichan.Infrastructure: grabacion/exportacion nativa.
- Bandichan.DebugRunner: runner headless para reproducir escenarios de export.
- Bandichan.Tests: base de pruebas unitarias xUnit.

### Capas y responsabilidades

- App llama interfaces de Core.
- Infrastructure implementa IRecordingService e IVideoPostProcessor.
- Core no depende de UI ni de infraestructura concreta.

## Tecnologias

- .NET 10
- WPF (net10.0-windows)
- Windows Forms (solo para tray y monitor discovery)
- ScreenRecorderLib
- Windows Media Editing / Media Foundation

## Requisitos

- Windows 10/11
- SDK .NET 10 instalado
- Arquitectura x64

## Configuracion de usuario

Bandichan guarda configuracion en:

- %LOCALAPPDATA%/Bandichan/settings.json

Campos principales:

- OutputDirectory
- EncoderMode
- CaptureFrameRate
- RecordSystemAudio
- SystemAudioDevice
- RunAtStartup
- HasShownTrayVisibilityHint

## Flujo de uso rapido

1. Ejecuta la app.
2. Usa icono de bandeja para Start Recording (o Import Video para editar un archivo existente).
3. Selecciona monitor.
  - El monitor donde esta visible el selector se marca como: "This display (where this window is shown)".
4. Stop Recording desde bandeja.
5. Se abre EditorWindow.
6. Ajusta crop/resize/cortes.
7. Export.

## Comandos de desarrollo

### Restaurar y compilar

```powershell
dotnet restore
dotnet build .\Bandichan.slnx -c Debug
```

### Ejecutar app

```powershell
dotnet run --project .\Bandichan.App\Bandichan.App.csproj
```

Nota: si `dotnet run` devuelve codigo `1` de forma intermitente, normalmente hay una instancia previa viva en tray por el modo single-instance.

### Ejecutar tests

```powershell
dotnet test .\Bandichan.Tests\Bandichan.Tests.csproj -c Debug
```

### Publicar Release (self-contained multi-file, recomendado)

```powershell
dotnet publish .\Bandichan.App\Bandichan.App.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

Salida tipica:

- publish/win-x64/
- Artefacto final principal: Bandichan.App.exe

Nota:

- El publish actual no usa single-file por compatibilidad con dependencias nativas de captura.
- Se limitan recursos satelite a `en;es` para reducir carpetas de idioma.

## Runner de diagnostico (headless)

Bandichan.DebugRunner permite reproducir exportaciones sin UI.

Ejemplo:

```powershell
dotnet run --project .\Bandichan.DebugRunner\Bandichan.DebugRunner.csproj -- `
  --source "C:\ruta\input.mp4" `
  --crop --crop-x 8 --crop-y 157 --crop-w 1337 --crop-h 808 `
  --resize --resize-w 1280 --resize-h 720 `
  --keep-audio --max-attempts 1 --out .\debug-matrix\case
```

## Detalles del pipeline de export

### Sin ediciones

- Ruta rapida por transcode directo cuando aplica.

### Con ediciones (crop/resize/cuts)

- Construccion de composicion por segmentos.
- Para crop:
  - clip base mantiene audio/timeline.
  - overlays se usan para recorte visual y canvas.
- Reintentos de compatibilidad de perfil y render.
- Objetivo: preservar ediciones sin rutas destructivas.

### Objetivos de tamano

- Preset 8MB:
  - codec seleccionable (H.264/H.265)
  - objetivo de tamano para Discord
  - limite duro de 10MB con pasada correctiva automatica si el resultado lo excede
- Presets Custom:
  - variantes H.264 y H.265
  - priorizan calidad manteniendo control de tamano

## Troubleshooting

### Build falla con DLL bloqueada

Sintoma:

- MSB3021 / MSB3027 sobre Bandichan.Infrastructure.dll.

Solucion:

```powershell
Get-Process -Name Bandichan.App -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build .\Bandichan.slnx -c Debug
```

### dotnet run finaliza con codigo 1

Sintoma:

- `dotnet run --project .\Bandichan.App\Bandichan.App.csproj` termina sin abrir una segunda UI.

Contexto:

- Es esperado cuando ya hay una instancia activa (single-instance con mutex global).

Solucion:

```powershell
Get-Process -Name Bandichan.App -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet run --project .\Bandichan.App\Bandichan.App.csproj
```

### No aparecen monitores

- Verifica permisos de escritorio y sesion local activa.
- Evita ejecutar en contextos sin escritorio interactivo.

### Audio de sistema no se graba

- Activa Record system audio en Settings.
- Prueba Audio test en Settings.
- Selecciona Auto loopback o dispositivo especifico.

### Could not start recording

- Bandichan intenta arranque con varias combinaciones de compatibilidad:
  - con/sin audio de sistema
  - hardware encoding on/off
- Si un monitor falla al iniciar, la app reintenta en monitor primario.

### Export con crop da problemas en una maquina

- Prueba preset Balanced.
- Mantiene resize moderado (ej. 1280x720).
- Usa DebugRunner para aislar caso con input fijo.

## Limitaciones conocidas

- El pipeline nativo de Windows puede variar segun stack de codecs/driver.
- Los escenarios mas sensibles son crop + media exotica + audio.
- El proyecto prioriza estabilidad en export final y control de fallback.
- El modo single-file puede romper dependencias nativas de captura en algunos equipos.

## Estructura de carpetas recomendada

- debug-matrix/: salidas de pruebas reproducibles.
- debug-export/: salidas del runner de grabacion/export.
- publish/: artefactos de release.

## Convenciones del repositorio

- Plataforma forzada a x64 via Directory.Build.props.
- WPF app en Bandichan.App.
- Interfaces y modelos en Bandichan.Core.
- Implementaciones nativas en Bandichan.Infrastructure.

## Roadmap tecnico sugerido

- Incrementar cobertura de tests para reglas de export (plan/normalizacion).
- Telemetria local opcional de stages de export (sin datos sensibles).
- Suite de casos de regresion en DebugRunner (crop/audio/cuts).

## Licencias y terceros

Dependencias principales:

- ScreenRecorderLib
- Microsoft.Windows.SDK.NET
- CommunityToolkit.Mvvm
- xUnit + Microsoft.NET.Test.Sdk + coverlet

Revisar sus licencias en NuGet segun politica del proyecto.

## Colaboracion

1. Crea rama de feature.
2. Reproduce caso con DebugRunner si aplica.
3. Compila App + ejecuta tests.
4. Adjunta comandos y salida relevante en PR.

## Nota final

Este README documenta el estado tecnico actual del proyecto y el flujo recomendado de operacion para desarrollo y diagnostico.
