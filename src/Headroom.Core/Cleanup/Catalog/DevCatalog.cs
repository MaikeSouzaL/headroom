using System.Globalization;
using System.Text.RegularExpressions;
using Headroom.Core.Cleanup.Rules;
using Headroom.Core.SystemTools;
using static Headroom.Core.Cleanup.Catalog.RuleBuilder;

namespace Headroom.Core.Cleanup.Catalog;

/// <summary>Caches de gerenciadores de pacotes, IDEs, Android, Docker/WSL, IA/ML e navegadores de teste.</summary>
internal static class DevCatalog
{
    private const CleanupCategory Dev = CleanupCategory.Developer;

    public static IEnumerable<ICleanupRule> Create()
    {
        // ---------- Gerenciadores de pacotes ----------
        yield return Def("dev.npm", Dev, "npm: cache", "Cache de pacotes do npm (_cacache) e logs. Pacotes são baixados de novo quando necessário.")
            .Contents(@"%LOCALAPPDATA%\npm-cache\_cacache").Contents(@"%LOCALAPPDATA%\npm-cache\_logs").Contents(@"%APPDATA%\npm-cache\_cacache")
            .Detect(@"%LOCALAPPDATA%\npm-cache", @"%APPDATA%\npm-cache").Build();

        yield return Def("dev.yarn", Dev, "Yarn: cache", "Cache global do Yarn (v1 e Berry).")
            .Contents(@"%LOCALAPPDATA%\Yarn\Cache").Contents(@"%LOCALAPPDATA%\Yarn\Berry\cache")
            .Detect(@"%LOCALAPPDATA%\Yarn").Build();

        yield return new CommandRule("dev.pnpm", Dev, "pnpm: pacotes não usados do store",
            "Remove do store global do pnpm só os pacotes que nenhum projeto usa (pnpm store prune).", RiskLevel.Safe)
        {
            RequiredExecutable = "pnpm",
            ExecutableFallbacks = [@"%APPDATA%\npm\pnpm.cmd", @"%LOCALAPPDATA%\pnpm\pnpm.exe"],
            Steps = [new CommandStep("{exe}", "store prune")],
            EstimatePaths = [@"%LOCALAPPDATA%\pnpm\store"],
            ActionText = "Estimativa = tamanho total do store; só o que não é usado sai.",
            AlwaysPermanent = true,
        };

        yield return Def("dev.bun", Dev, "Bun: cache", "Cache de instalação do Bun.")
            .Contents(@"%USERPROFILE%\.bun\install\cache").Build();

        yield return new CommandRule("dev.nuget", Dev, "NuGet: caches e pacotes globais",
            "dotnet nuget locals all --clear: limpa a pasta global de pacotes, caches HTTP e temporários. " +
            "Os pacotes são baixados de novo no próximo build (precisa de internet).", RiskLevel.Review)
        {
            RequiredExecutable = "dotnet",
            Steps = [new CommandStep("{exe}", "nuget locals all --clear", TimeoutMinutes: 30)],
            EstimatePaths = [@"%USERPROFILE%\.nuget\packages", @"%LOCALAPPDATA%\NuGet\v3-cache", @"%LOCALAPPDATA%\NuGet\http-cache",
                @"%LOCALAPPDATA%\NuGet\plugins-cache", @"%TEMP%\NuGetScratch"],
            DetectPath = @"%USERPROFILE%\.nuget",
            EnabledByDefault = false,
        };

        yield return Def("dev.pip", Dev, "pip: cache", "Cache de downloads e wheels do pip.")
            .Contents(@"%LOCALAPPDATA%\pip\cache").Build();

        yield return new UvCacheRule(Def("dev.uv", Dev, "uv: cache", "Cache do gerenciador Python uv (pacotes, builds e índices).")
            .Contents(@"%LOCALAPPDATA%\uv\cache").Close("uv.exe"));

        yield return Def("dev.poetry", Dev, "Poetry: cache", "Cache de pacotes e artefatos do Poetry.")
            .Contents(@"%LOCALAPPDATA%\pypoetry\Cache").Build();

        yield return new CommandRule("dev.conda", Dev, "Conda: pacotes e caches",
            "conda clean -a -y: remove pacotes não usados, tarballs, índices e caches do Conda/Anaconda/Miniconda.", RiskLevel.Safe)
        {
            RequiredExecutable = "conda",
            ExecutableFallbacks = [@"%USERPROFILE%\anaconda3\Scripts\conda.exe", @"%USERPROFILE%\miniconda3\Scripts\conda.exe",
                @"%PROGRAMDATA%\anaconda3\Scripts\conda.exe", @"%PROGRAMDATA%\miniconda3\Scripts\conda.exe", @"%LOCALAPPDATA%\miniforge3\Scripts\conda.exe"],
            Steps = [new CommandStep("{exe}", "clean -a -y", TimeoutMinutes: 30)],
            EstimatePaths = [@"%USERPROFILE%\anaconda3\pkgs", @"%USERPROFILE%\miniconda3\pkgs", @"%USERPROFILE%\.conda\pkgs", @"%LOCALAPPDATA%\miniforge3\pkgs"],
            ActionText = "Estimativa = tamanho da pasta pkgs; pacotes em uso por ambientes ficam.",
        };

        yield return Def("dev.maven", Dev, "Maven: repositório local (.m2)",
                "Todas as dependências baixadas pelo Maven. O próximo build baixa de novo o que precisar.", RiskLevel.Review)
            .Contents(@"%USERPROFILE%\.m2\repository").Off().Build();

        yield return Def("dev.gradle.caches", Dev, "Gradle: caches", "Caches de dependências e builds do Gradle (recriados no próximo build).")
            .Contents(@"%USERPROFILE%\.gradle\caches").Contents(@"%USERPROFILE%\.gradle\daemon", minAgeDays: 1)
            .Close("java.exe").Build();

        yield return new OldVersionsRule("dev.gradle.wrapper", Dev, "Gradle: versões antigas do Wrapper",
            "Distribuições antigas do Gradle baixadas pelo gradlew (mantém a mais nova de cada tipo).", RiskLevel.Safe,
            @"%USERPROFILE%\.gradle\wrapper\dists", OldVersionsRule.ParseGradleDist);

        yield return Def("dev.cargo", Dev, "Cargo (Rust): cache do registro", "Pacotes baixados e fontes extraídas do crates.io e checkouts git.")
            .Contents(@"%USERPROFILE%\.cargo\registry\cache").Contents(@"%USERPROFILE%\.cargo\registry\src").Contents(@"%USERPROFILE%\.cargo\git\checkouts")
            .Detect(@"%USERPROFILE%\.cargo").Build();

        yield return new CommandRule("dev.go", Dev, "Go: cache de módulos e de build",
            "go clean -cache -modcache: apaga o cache de compilação e os módulos baixados (baixados de novo quando necessário).", RiskLevel.Safe)
        {
            RequiredExecutable = "go",
            ExecutableFallbacks = [@"%PROGRAMFILES%\Go\bin\go.exe"],
            Steps = [new CommandStep("{exe}", "clean -cache -modcache", TimeoutMinutes: 30)],
            EstimatePaths = [@"%USERPROFILE%\go\pkg\mod", @"%LOCALAPPDATA%\go-build"],
        };

        yield return Def("dev.composer", Dev, "Composer (PHP): cache", "Cache de pacotes do Composer.")
            .Contents(@"%LOCALAPPDATA%\Composer\files").Contents(@"%LOCALAPPDATA%\Composer\repo").Contents(@"%LOCALAPPDATA%\Composer\vcs")
            .Detect(@"%LOCALAPPDATA%\Composer").Build();

        yield return Def("dev.pub", Dev, "Dart/Flutter: cache do pub", "Pacotes baixados pelo pub (flutter pub get baixa de novo).", RiskLevel.Review)
            .Contents(@"%LOCALAPPDATA%\Pub\Cache").Off().Build();

        // ---------- Projetos ----------
        yield return new StaleProjectsRule();

        // ---------- IDEs ----------
        foreach (var (key, name, folder, process) in new[]
                 {
                     ("vscode", "VS Code", "Code", "Code.exe"),
                     ("vscodeinsiders", "VS Code Insiders", "Code - Insiders", "Code - Insiders.exe"),
                     ("vscodium", "VSCodium", "VSCodium", "VSCodium.exe"),
                     ("cursor", "Cursor", "Cursor", "Cursor.exe"),
                     ("windsurf", "Windsurf", "Windsurf", "Windsurf.exe"),
                 })
        {
            string root = $@"%APPDATA%\{folder}";
            yield return Def($"dev.{key}.cache", Dev, $"{name}: cache e logs", $"Cache, dados em cache, VSIX baixados e logs do {name}.")
                .Contents($@"{root}\Cache").Contents($@"{root}\CachedData").Contents($@"{root}\CachedExtensionVSIXs")
                .Contents($@"{root}\CachedProfilesData").Contents($@"{root}\Code Cache").Contents($@"{root}\GPUCache")
                .Contents($@"{root}\Service Worker\CacheStorage").Contents($@"{root}\logs", minAgeDays: 1)
                .Close(process).Detect(root).Build();
        }

        yield return new VsCodeWorkspaceStorageRule();

        yield return new OldVersionsRule("dev.vscode.oldextensions", Dev, "VS Code: versões antigas de extensões",
            "Versões anteriores de extensões que ficaram no disco após atualizações (as referenciadas pelo VS Code são mantidas).",
            RiskLevel.Safe, @"%USERPROFILE%\.vscode\extensions", OldVersionsRule.ParseVsCodeExtension)
        {
            ProtectedNames = OldVersionsRule.VsCodeReferencedExtensions,
            ProcessesToClose = ["Code.exe"],
        };

        yield return Def("dev.jetbrains.caches", Dev, "JetBrains: caches e logs",
                "Caches, índices e logs das IDEs JetBrains (IntelliJ, PyCharm, Rider, WebStorm...). A IDE reindexa ao abrir.")
            .Contents(@"%LOCALAPPDATA%\JetBrains\*\caches").Contents(@"%LOCALAPPDATA%\JetBrains\*\log").Contents(@"%LOCALAPPDATA%\JetBrains\*\tmp")
            .Close("idea64.exe", "pycharm64.exe", "rider64.exe", "webstorm64.exe", "clion64.exe", "goland64.exe", "phpstorm64.exe", "datagrip64.exe", "studio64.exe")
            .Detect(@"%LOCALAPPDATA%\JetBrains").Build();

        yield return new OldVersionsRule("dev.jetbrains.oldlocal", Dev, "JetBrains: dados de versões antigas das IDEs",
            "Pastas de cache/sistema de versões antigas das IDEs (ex.: IntelliJIdea2023.1 quando já existe a 2024.x).", RiskLevel.Review,
            @"%LOCALAPPDATA%\JetBrains", OldVersionsRule.ParseJetBrains)
        { ItemsSelectedByDefault = false, EnabledByDefault = false };

        yield return new OldVersionsRule("dev.jetbrains.oldconfig", Dev, "JetBrains: configurações de versões antigas",
            "Configurações de versões antigas das IDEs (já migradas para a versão nova).", RiskLevel.Review,
            @"%APPDATA%\JetBrains", OldVersionsRule.ParseJetBrains)
        { ItemsSelectedByDefault = false, EnabledByDefault = false };

        yield return Def("dev.visualstudio", Dev, "Visual Studio: caches", "ComponentModelCache, cache de designer e temporários do Visual Studio.")
            .Contents(@"%LOCALAPPDATA%\Microsoft\VisualStudio\*\ComponentModelCache")
            .Contents(@"%LOCALAPPDATA%\Microsoft\VisualStudio\*\Designer\ShadowCache")
            .Contents(@"%LOCALAPPDATA%\Microsoft\VisualStudio\*\ImageLibrary")
            .Contents(@"%TEMP%\VSFeedbackIntelliCodeLogs").Contents(@"%LOCALAPPDATA%\Temp\VSRemoteControl")
            .Close("devenv.exe").Detect(@"%LOCALAPPDATA%\Microsoft\VisualStudio").Build();

        // ---------- Android ----------
        yield return new AndroidSystemImagesRule();

        yield return Def("dev.android.avds", Dev, "Android: dispositivos virtuais (AVDs)",
                "Cada emulador aparece separado. Nada vem marcado: escolha os AVDs que não usa mais.", RiskLevel.Review)
            .Children(@"%USERPROFILE%\.android\avd", pattern: "*.avd").Unselected().Off().Close("qemu-system-x86_64.exe", "emulator.exe").Build();

        yield return new OldVersionsRule("dev.android.buildtools", Dev, "Android: build-tools antigos",
            "Versões antigas do build-tools do Android SDK (mantém a mais nova).", RiskLevel.Review,
            @"%ANDROIDSDK%\build-tools", OldVersionsRule.ParseAndroidVersion)
        { EnabledByDefault = false };

        yield return new OldVersionsRule("dev.android.platforms", Dev, "Android: plataformas antigas do SDK",
            "Plataformas (API levels) antigas do Android SDK (mantém as duas mais novas).", RiskLevel.Review,
            @"%ANDROIDSDK%\platforms", OldVersionsRule.ParseAndroidVersion, keep: 2)
        { EnabledByDefault = false, ItemsSelectedByDefault = false };

        yield return Def("dev.gradle.androidcaches", Dev, "Android Studio: caches",
                "Caches e logs do Android Studio (Google).")
            .Contents(@"%LOCALAPPDATA%\Google\AndroidStudio*\caches").Contents(@"%LOCALAPPDATA%\Google\AndroidStudio*\log")
            .Close("studio64.exe").Detect(@"%LOCALAPPDATA%\Google\AndroidStudio*").Build();

        // ---------- Docker / WSL ----------
        yield return DockerRule("dev.docker.prune", "Docker: imagens, contêineres e redes sem uso",
            "docker system prune -a -f: remove contêineres parados, redes sem uso, imagens sem contêiner e cache de build.",
            RiskLevel.Review, "system prune -a -f", "Images|Containers|Build Cache");
        yield return DockerRule("dev.docker.builder", "Docker: cache de build",
            "docker builder prune -a -f: remove todo o cache de build (BuildKit).", RiskLevel.Safe, "builder prune -a -f", "Build Cache");
        yield return DockerRule("dev.docker.volumes", "Docker: volumes órfãos",
            "docker volume prune -f: remove volumes que nenhum contêiner usa. ATENÇÃO: bancos de dados em volumes órfãos são perdidos.",
            RiskLevel.Review, "volume prune -f", "Local Volumes");
        yield return new VhdxCompactRule();

        // ---------- IA / ML ----------
        yield return Def("dev.ai.huggingface", Dev, "IA: modelos e datasets do Hugging Face",
                "Cada modelo/dataset do cache do Hugging Face aparece separado com tamanho e data. Nada vem marcado.", RiskLevel.Review)
            .Children(@"%USERPROFILE%\.cache\huggingface\hub", pattern: "models--*").Children(@"%USERPROFILE%\.cache\huggingface\hub", pattern: "datasets--*")
            .Unselected().Off().Build();

        yield return new OllamaModelsRule();

        yield return Def("dev.ai.lmstudio", Dev, "IA: modelos do LM Studio",
                "Cada modelo baixado pelo LM Studio aparece separado. Nada vem marcado.", RiskLevel.Review)
            .Children(@"%USERPROFILE%\.lmstudio\models\*").Children(@"%USERPROFILE%\.cache\lm-studio\models\*")
            .Unselected().Off().Close("LM Studio.exe").Build();

        yield return Def("dev.ai.torchhub", Dev, "IA: cache do PyTorch Hub", "Modelos e repositórios baixados pelo torch.hub.", RiskLevel.Review)
            .Children(@"%USERPROFILE%\.cache\torch\hub").Unselected().Off().Build();

        // ---------- Navegadores de teste ----------
        yield return Def("dev.playwright", Dev, "Playwright: navegadores baixados",
                "Navegadores do Playwright (baixados de novo com \"npx playwright install\").", RiskLevel.Review)
            .Children(@"%LOCALAPPDATA%\ms-playwright").Off().Build();

        yield return Def("dev.puppeteer", Dev, "Puppeteer: navegadores baixados", "Chrome/Firefox baixados pelo Puppeteer.", RiskLevel.Review)
            .Children(@"%USERPROFILE%\.cache\puppeteer").Off().Build();

        yield return Def("dev.cypress", Dev, "Cypress: binários em cache", "Versões do Cypress em cache (baixadas de novo pelo npm).", RiskLevel.Review)
            .Children(@"%LOCALAPPDATA%\Cypress\Cache").Off().Build();

        yield return Def("dev.electron", Dev, "Electron: cache de downloads", "Binários do Electron baixados pelo electron/electron-builder.")
            .Contents(@"%LOCALAPPDATA%\electron\Cache").Contents(@"%LOCALAPPDATA%\electron-builder\Cache")
            .Detect(@"%LOCALAPPDATA%\electron", @"%LOCALAPPDATA%\electron-builder").Build();
    }

    private static CommandRule DockerRule(string id, string name, string description, RiskLevel risk, string arguments, string reclaimTypes) =>
        new(id, Dev, name, description, risk)
        {
            RequiredExecutable = "docker",
            ExecutableFallbacks = [@"%PROGRAMFILES%\Docker\Docker\resources\bin\docker.exe"],
            Steps = [new CommandStep("{exe}", arguments, TimeoutMinutes: 60)],
            EnabledByDefault = false,
            Estimator = async (context, ct) =>
            {
                string? docker = CommandRunner.FindOnPath("docker") ??
                                 context.Paths.Expand(@"%PROGRAMFILES%\Docker\Docker\resources\bin\docker.exe").FirstOrDefault();
                if (docker is null)
                {
                    return (-1, "Docker não encontrado.");
                }

                var run = await CommandRunner.RunAsync(docker, "system df --format \"{{.Type}}|{{.Reclaimable}}\"", TimeSpan.FromSeconds(30),
                    cancellationToken: ct).ConfigureAwait(false);
                if (!run.Success)
                {
                    return (-1, "O Docker não está em execução. Abra o Docker Desktop e analise de novo.");
                }

                long total = 0;
                foreach (var line in run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('|');
                    if (parts.Length == 2 && Regex.IsMatch(parts[0].Trim(), reclaimTypes, RegexOptions.IgnoreCase))
                    {
                        total += ParseDockerSize(parts[1]);
                    }
                }

                return (total, "Espaço recuperável informado pelo \"docker system df\".");
            },
        };

    /// <summary>"2.5GB (50%)" → bytes (unidades decimais do Docker).</summary>
    internal static long ParseDockerSize(string text)
    {
        var m = Regex.Match(text, @"([\d.]+)\s*([kKMGT]?B)");
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return 0;
        }

        double multiplier = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            _ => 1,
        };
        return (long)(value * multiplier);
    }
}
