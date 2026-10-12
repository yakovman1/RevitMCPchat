# Revit MCP Chat

Revit MCP Chat is a separate, dockable Revit add-in for Revit 2022, 2023 and 2024. This release does not support Revit 2025 or newer. It provides an AI chat, model-scoped chat history, provider settings and confirmation prompts for model-changing MCP tools.

The add-in targets .NET Framework 4.8. Its local .NET 8 agent starts the separately installed [`rvt-mcp`](https://github.com/yakovman1/rvt-mcp) server and talks to it over MCP stdio. Revit MCP Chat does not install or modify `rvt-mcp`.

## Install

Download and extract the release ZIP, then run `install.ps1` in PowerShell. The installer detects installed Revit 2022–2024 versions, copies only the chat add-in and agent files, and writes a manifest for each detected version. It does not overwrite user history, settings or DPAPI-protected keys.

Install `rvt-mcp` separately using its own instructions. The expected server path is `%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe`. If it is missing, the chat installer prints the upstream install link and continues installing the chat add-in. If Revit is open, the installer checks MCP initialization and the Revit tool catalog. Otherwise, the same checks run when the chat reconnects inside Revit.

See [install.md](docs/install.md) for detailed setup and removal instructions.

## AI services

Built-in profiles are provided for OpenAI, Anthropic, Gemini, DeepSeek, Yandex AI Studio/Alice AI, Sber GigaChat and OpenAI-compatible endpoints. Other protocols need an adapter. Model IDs and endpoint addresses can be edited in Settings.

User API keys are protected with Windows DPAPI for the current Windows account. If a user key and an administrator key both exist for the same provider, the user key takes priority. The interface and settings file only expose whether a key exists and which source is active.

## Administrator-provided keys

Administrators can build a private package with a local JSON file that maps provider IDs to keys:

```json
{
  "openai": "replace-with-a-real-key",
  "gemini": "replace-with-another-key"
}
```

Run `packaging/build-release.ps1 -Configuration Release -AdminKeysFile <local-json-path>`. The build generates a temporary C# key source, compiles it into the private agent, and removes the temporary source after the build. The key is not placed in the UI or settings configuration. A key compiled into a client binary can still be extracted by someone who can inspect that binary or its process memory; use a server-side proxy if the key must remain secret from users.

The public build omits the generated key source. Do not commit the key file or distribute a private package beyond its intended audience.

## Release validation

See [docs/test-matrix.md](docs/test-matrix.md) for automated results, live-host checks and known validation limits. Live Revit tests are reported separately from tests that use simulated MCP servers.

## Build and verify

```powershell
dotnet test RevitMCPchat.slnx
dotnet build RevitMCPchat/RevitMCPchat.csproj -p:RevitYear=2022
dotnet build RevitMCPchat/RevitMCPchat.csproj -p:RevitYear=2023
dotnet build RevitMCPchat/RevitMCPchat.csproj -p:RevitYear=2024
./packaging/build-release.ps1 -Configuration Release
$package = Get-ChildItem ./packaging/artifacts/RevitMcpChat-*-win-x64.zip | Select-Object -First 1
Expand-Archive $package.FullName ./packaging/artifacts/verify
./packaging/verify-package.ps1 -PackageRoot ./packaging/artifacts/verify
```

The release script writes a versioned ZIP under `packaging/artifacts`. Revit MCP Chat and `rvt-mcp` are released and updated independently.

## Подробная инструкция: сборка, установка и проверка

Этот раздел описывает полный путь от исходного кода до проверки панели в Revit. Если вы скачали готовый ZIP из Releases, перейдите сразу к шагу 4.

### 1. Что понадобится

- Windows и Visual Studio с поддержкой проектов `.slnx` и рабочей нагрузкой **Разработка классических приложений .NET**.
- .NET 8 SDK для локального агента, тестов и сборочного скрипта. При первом восстановлении NuGet-пакетов нужен доступ к интернету.
- Revit 2022, 2023 или 2024 для проверки внутри Revit. Устанавливать Revit на машину сборки для компиляции не требуется: ссылки на Revit API восстанавливаются как NuGet-пакеты.
- Отдельно установленный [`rvt-mcp`](https://github.com/yakovman1/rvt-mcp). Плагин чата не содержит этот сервер и не меняет его конфигурацию.
- Тестовая копия модели Revit. Используйте отдельную копию, когда проверяете операции, которые меняют модель.
- API-ключ выбранного ИИ-сервиса. Для подключения также нужны корректный идентификатор модели и, для пользовательского endpoint, адрес API.

### 2. Получить исходный код и собрать проект

Склонируйте репозиторий и откройте файл решения в Visual Studio:

```powershell
git clone https://github.com/yakovman1/RevitMCPchat.git
Set-Location .\RevitMCPchat
```

Откройте `RevitMCPchat.slnx`, выберите конфигурацию `Release` и выполните **Сборка → Собрать решение**. В проекте по умолчанию выбрана цель Revit 2022, поэтому эта команда собирает вариант для Revit 2022. Для сборки каждого поддерживаемого года выполните в терминале из корня репозитория:

```powershell
dotnet build .\RevitMCPchat\RevitMCPchat.csproj -c Release -p:RevitYear=2022
dotnet build .\RevitMCPchat\RevitMCPchat.csproj -c Release -p:RevitYear=2023
dotnet build .\RevitMCPchat\RevitMCPchat.csproj -c Release -p:RevitYear=2024
```

Запустите автоматические тесты:

```powershell
dotnet test .\RevitMCPchat.slnx -c Release
```

Чтобы установить плагин в Revit, одной команды **Собрать решение** недостаточно: она не создаёт установочный ZIP и не размещает файлы в папках Revit. Используйте сборку пакета на следующем шаге.

### 3. Создать и проверить установочный ZIP

Из корня репозитория выполните:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\packaging\build-release.ps1 `
  -Configuration Release `
  -OutputDirectory .\packaging\artifacts `
  -Version 0.1.0
```

Скрипт соберёт варианты надстройки для Revit 2022, 2023 и 2024, автономный локальный агент и установочные файлы. Архив появится в `packaging\artifacts`, например `RevitMcpChat-0.1.0-win-x64.zip`. Параметр `-Version` задаёт номер версии в имени архива.

Проверьте содержимое архива после распаковки:

```powershell
$zip = Get-Item .\packaging\artifacts\RevitMcpChat-0.1.0-win-x64.zip
$checkDirectory = Join-Path $env:TEMP 'RevitMcpChat-package-check'
Expand-Archive -LiteralPath $zip.FullName -DestinationPath $checkDirectory -Force
pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $checkDirectory 'verify-package.ps1') `
  -PackageRoot $checkDirectory
```

При успехе скрипт сообщит, что структура пакета подходит для Revit 2022, 2023 и 2024. Эта проверка подтверждает состав архива; она не заменяет запуск плагина в Revit.

### 4. Установить пакет в Revit

Сначала установите `rvt-mcp` по его инструкции. По умолчанию чат ищет исполняемый файл сервера здесь:
`%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe`.

Закройте Revit, распакуйте ZIP в постоянную локальную папку и откройте PowerShell в корне распакованного пакета. Установку запускайте **от имени своей обычной учётной записи Windows**, не от имени другого пользователя: пользовательские настройки и API-ключи привязаны к учётной записи Windows.

Проверьте пакет и установите надстройку для Revit 2024:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\verify-package.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -RevitYears 2024
```

Для установки во все найденные установщиком версии Revit запустите `install.ps1` без параметра `-RevitYears`. Если Revit установлен в необычную папку и автоопределение его не находит, укажите нужные годы явно, например:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -RevitYears 2022,2023,2024
```

Установщик размещает файлы надстройки в `%LOCALAPPDATA%\RevitMcpChat`, а манифесты Revit — в `%APPDATA%\Autodesk\Revit\Addins\<год>`. Он проверяет структуру ZIP и, если Revit уже запущен, пытается выполнить MCP-проверку. Если Revit закрыт, эта проверка произойдёт при подключении из панели.

Запустите Revit и выберите **Надстройки → Revit MCP Chat → AI Chat**. В появившейся закрепляемой панели откройте вкладку **Настройки**:

1. Выберите провайдера ИИ.
2. Укажите идентификатор модели; при необходимости проверьте адрес API и дополнительные поля выбранного провайдера.
3. Вставьте свой API-ключ и нажмите **Сохранить настройки**. Поле ключа очистится. Пользовательский ключ шифруется Windows DPAPI и расшифровывается только в той же учётной записи Windows. Если для провайдера в частной сборке есть ключ администратора, сохранённый пользователем ключ имеет приоритет.
4. Вернитесь во вкладку **Чат**, нажмите **Подключить** и убедитесь, что панель показывает подключение к `rvt-mcp` и доступный каталог инструментов Revit.

### 5. Проверить работу чата и режимы MCP

Начните с модели-копии и запроса, который только читает данные, например: «Сколько уровней есть в этой модели? Перечисли их названия». Убедитесь, что ответ получен через доступные инструменты Revit.

Для безопасной первой проверки настройте `rvt-mcp` в его собственном режиме `read_only` по инструкции upstream-проекта, а затем перезапустите Revit и подключение. В этом режиме ограничения списка и выполнения инструментов задаёт сам `rvt-mcp`; чат не меняет эту настройку. Запросите чтение модели и убедитесь, что инструменты изменения модели недоступны или отклоняются сервером.

Затем, только на копии модели, проверьте режим с операциями изменения. Переключите настройку `read_only` самого `rvt-mcp` согласно его инструкции и переподключитесь. Попросите выполнить небольшое обратимое изменение. Перед вызовом инструмента чат должен показать запрос с инструментом и аргументами. Сначала нажмите **Отклонить** и проверьте, что модель не изменилась. При необходимости повторите тест и подтвердите безопасную операцию, затем проверьте результат в Revit. Вызов `send_code` всегда требует отдельного подтверждения.

Также проверьте, что новая беседа отображается во вкладке **История**, открывается после перезапуска Revit и привязана к той модели, где была создана. Проверьте смену провайдера или модели только после сохранения соответствующего профиля в настройках.

### 6. Диагностика и удаление

- **Панель не появилась.** Закройте Revit и проверьте, что установщик завершился без ошибки и создал манифест `%APPDATA%\Autodesk\Revit\Addins\<год>\RevitMcpChat.addin`. Запустите Revit повторно.
- **Панель открылась, но нет подключения или инструментов Revit.** Убедитесь, что `rvt-mcp` установлен отдельно и его сервер находится по ожидаемому пути. Нажмите **Подключить**. При ошибке MCP-handshake обновите `rvt-mcp` до совместимой версии и перезапустите Revit.
- **Установщик не нашёл Revit.** Передайте `-RevitYears`, например `-RevitYears 2024`.
- **В `read_only` изменение не выполняется.** Это ожидаемо, если выбранный инструмент отсутствует в каталоге или сервер запрещает вызов. Для теста с подтверждением используйте копию модели и режим `rvt-mcp`, разрешающий соответствующий инструмент.

Чтобы удалить плагин, закройте Revit и из папки распакованного пакета запустите:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Обычное удаление сохраняет историю, настройки и DPAPI-ключи пользователя. Параметр `-Purge` удаляет известные данные Revit MCP Chat, включая историю, настройки и ключи. Сам `rvt-mcp` удаляется отдельно. Дополнительные сведения: [инструкция по установке](docs/install.md) и [матрица проверок](docs/test-matrix.md).

Если вы действительно хотите удалить пользовательские данные чата вместе с плагином, запустите вместо обычной команды:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\uninstall.ps1 -Purge
```

## Validation limits

Automated tests cover the local agent, provider protocols, IPC, view models and package scripts. Loading the add-in and exercising Revit tools still requires a live Revit 2022–2024 installation with a compatible `rvt-mcp` add-in and server.
