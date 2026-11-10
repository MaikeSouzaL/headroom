using System.Globalization;
using System.Text;
using Headroom.Core.Formatting;
using Headroom.Core.Scanning;

namespace Headroom.Core.Reports;

/// <summary>Explicação padrão da diferença entre o total escaneado e o "usado" do disco.</summary>
public static class ScanExplanations
{
    public static string DifferenceText(ScanResult result)
    {
        if (result.DifferenceFromUsed is not long difference)
        {
            return "O scan foi de uma pasta; a comparação com o espaço usado do disco não se aplica.";
        }

        if (difference >= 0)
        {
            return $"O Windows informa {ByteSize.Format(result.Drive!.UsedBytes)} usados e o scan encontrou " +
                   $"{ByteSize.Format(result.TotalBytes)}. A diferença de {ByteSize.Format(difference)} vem de Shadow Copies " +
                   "(pontos de restauração), System Volume Information, metadados do NTFS (MFT, journal), arquivos de " +
                   $"outros usuários e pastas sem permissão ({ByteSize.FormatCount(result.InaccessibleCount)} pasta(s) puladas)" +
                   (result.WasElevated ? "." : " — rodar como administrador reduz essa diferença.");
        }

        return $"O scan encontrou {ByteSize.Format(-difference)} a mais do que o Windows informa como usado. Isso acontece " +
               "porque arquivos com hardlinks (comuns em C:\\Windows\\WinSxS) e arquivos compactados pelo NTFS são " +
               "contados pelo tamanho lógico, mais de uma vez ou maior do que ocupam de fato.";
    }
}

/// <summary>Exporta o relatório do scan em HTML autocontido (abre em qualquer navegador).</summary>
public static class HtmlReportExporter
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static void Export(ScanResult result, string filePath, IReadOnlyList<RecoverableItem>? recoverable = null,
        int maxFiles = 200)
    {
        recoverable ??= RecoverableSpaceEstimator.Estimate(result);
        var sb = new StringBuilder(256 * 1024);
        string H(string s) => Html(s);

        sb.Append("<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>Headroom — {H(result.RootPath)}</title><style>");
        sb.Append(":root{--bg:#f6f7f9;--card:#fff;--text:#1b1f24;--muted:#5b6470;--line:#e3e6ea;--accent:#0f6cbd}");
        sb.Append("@media(prefers-color-scheme:dark){:root{--bg:#1b1d21;--card:#24272c;--text:#eceef1;--muted:#a3abb5;--line:#353a41;--accent:#4cc2ff}}");
        sb.Append("body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 'Segoe UI',system-ui,sans-serif}");
        sb.Append("main{max-width:1100px;margin:0 auto;padding:24px 16px}h1{font-size:26px;margin:0 0 4px}h2{font-size:18px;margin:28px 0 10px}");
        sb.Append(".muted{color:var(--muted)}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:12px;margin-top:16px}");
        sb.Append(".card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:14px}.card b{display:block;font-size:22px}");
        sb.Append("table{width:100%;border-collapse:collapse;background:var(--card);border:1px solid var(--line);border-radius:8px;overflow:hidden}");
        sb.Append("th,td{padding:6px 10px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}th{font-weight:600;background:rgba(127,127,127,.08)}");
        sb.Append("td.n{text-align:right;white-space:nowrap}td.p{word-break:break-all}.bar{height:8px;border-radius:4px;background:var(--line);min-width:120px}");
        sb.Append(".bar>span{display:block;height:8px;border-radius:4px}.note{background:var(--card);border-left:4px solid var(--accent);padding:10px 14px;border-radius:4px}");
        sb.Append("</style></head><body><main>");

        sb.Append($"<h1>Relatório de espaço — {H(result.RootPath)}</h1>");
        sb.Append($"<div class=\"muted\">Gerado pelo Headroom em {DateTime.Now.ToString("dd/MM/yyyy HH:mm", PtBr)} · ");
        sb.Append($"scan iniciado em {result.StartedAt.ToString("dd/MM/yyyy HH:mm", PtBr)} · duração {H(ByteSize.FormatDuration(result.Elapsed))}</div>");

        sb.Append("<div class=\"cards\">");
        Card(sb, "Total escaneado", ByteSize.Format(result.TotalBytes));
        if (result.Drive is not null)
        {
            Card(sb, "Livre no disco", ByteSize.Format(result.Drive.FreeBytes));
            Card(sb, "Tamanho do disco", ByteSize.Format(result.Drive.TotalBytes));
        }

        Card(sb, "Arquivos", ByteSize.FormatCount(result.FileCount));
        Card(sb, "Pastas", ByteSize.FormatCount(result.DirectoryCount));
        Card(sb, "Recuperável (estimado)", ByteSize.Format(recoverable.Sum(r => r.Bytes)));
        sb.Append("</div>");

        sb.Append($"<h2>Diferença em relação ao disco</h2><div class=\"note\">{H(ScanExplanations.DifferenceText(result))}</div>");

        sb.Append("<h2>Espaço por categoria</h2><table><tr><th>Categoria</th><th></th><th>Arquivos</th><th>Tamanho</th></tr>");
        long max = Math.Max(1, result.Categories.Max(c => c.TotalBytes));
        foreach (var c in result.Categories.Where(c => c.TotalBytes > 0))
        {
            string color = FileCategoryInfo.ColorHex(c.Category);
            double w = c.TotalBytes * 100.0 / max;
            sb.Append($"<tr><td>{H(c.DisplayName)}</td><td><div class=\"bar\"><span style=\"width:{w.ToString("0.#", CultureInfo.InvariantCulture)}%;background:{color}\"></span></div></td>");
            sb.Append($"<td class=\"n\">{ByteSize.FormatCount(c.FileCount)}</td><td class=\"n\">{ByteSize.Format(c.TotalBytes)}</td></tr>");
        }

        sb.Append("</table>");

        sb.Append("<h2>Maiores pastas (primeiro nível)</h2><table><tr><th>Pasta</th><th>%</th><th>Arquivos</th><th>Tamanho</th></tr>");
        foreach (var node in result.Root.Children.Where(n => n.IsDirectory).Take(30))
        {
            sb.Append($"<tr><td class=\"p\">{H(node.FullPath)}</td><td class=\"n\">{ByteSize.FormatPercent(node.FractionOfParent)}</td>");
            sb.Append($"<td class=\"n\">{ByteSize.FormatCount(node.FileCount)}</td><td class=\"n\">{ByteSize.Format(node.Size)}</td></tr>");
        }

        sb.Append("</table>");

        if (recoverable.Count > 0)
        {
            sb.Append("<h2>Espaço recuperável estimado</h2><table><tr><th>Item</th><th>Local</th><th>Tamanho</th></tr>");
            foreach (var item in recoverable.Take(50))
            {
                sb.Append($"<tr><td>{H(item.Label)}</td><td class=\"p\">{H(item.Path)}</td><td class=\"n\">{ByteSize.Format(item.Bytes)}</td></tr>");
            }

            sb.Append("</table>");
        }

        sb.Append($"<h2>Maiores arquivos (até {maxFiles})</h2><table><tr><th>Arquivo</th><th>Categoria</th><th>Modificado</th><th>Tamanho</th></tr>");
        foreach (var file in result.LargestFiles.Take(maxFiles))
        {
            sb.Append($"<tr><td class=\"p\">{H(file.FullPath)}</td><td>{H(FileCategoryInfo.DisplayName(file.Category))}</td>");
            sb.Append($"<td class=\"n\">{file.LastModified.ToString("dd/MM/yyyy", PtBr)}</td><td class=\"n\">{ByteSize.Format(file.Size)}</td></tr>");
        }

        sb.Append("</table>");

        sb.Append("<h2>Extensões que mais ocupam</h2><table><tr><th>Extensão</th><th>Categoria</th><th>Arquivos</th><th>Tamanho</th></tr>");
        foreach (var ext in result.Extensions.Take(30))
        {
            string name = string.IsNullOrEmpty(ext.Extension) ? "(sem extensão)" : ext.Extension;
            sb.Append($"<tr><td>{H(name)}</td><td>{H(FileCategoryInfo.DisplayName(ext.Category))}</td>");
            sb.Append($"<td class=\"n\">{ByteSize.FormatCount(ext.FileCount)}</td><td class=\"n\">{ByteSize.Format(ext.TotalBytes)}</td></tr>");
        }

        sb.Append("</table>");

        if (result.DevArtifacts.Count > 0)
        {
            sb.Append($"<h2>Artefatos de desenvolvimento ({ByteSize.Format(result.DevArtifactsBytes)})</h2><table><tr><th>Pasta</th><th>Tipo</th><th>Modificado</th><th>Tamanho</th></tr>");
            foreach (var a in result.DevArtifacts.Take(200))
            {
                sb.Append($"<tr><td class=\"p\">{H(a.Path)}</td><td>{H(a.KindName)}</td><td class=\"n\">{a.Node.LastModified.ToString("dd/MM/yyyy", PtBr)}</td><td class=\"n\">{ByteSize.Format(a.Size)}</td></tr>");
            }

            sb.Append("</table>");
        }

        if (result.InaccessibleSamples.Count > 0)
        {
            sb.Append($"<h2>Pastas sem permissão ({ByteSize.FormatCount(result.InaccessibleCount)})</h2><table>");
            foreach (var path in result.InaccessibleSamples.Take(100))
            {
                sb.Append($"<tr><td class=\"p\">{H(path)}</td></tr>");
            }

            sb.Append("</table>");
        }

        sb.Append("<p class=\"muted\" style=\"margin-top:28px\">Relatório gerado pelo Headroom. Os tamanhos são lógicos (base 1024).</p>");
        sb.Append("</main></body></html>");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false));
    }

    private static void Card(StringBuilder sb, string label, string value) =>
        sb.Append($"<div class=\"card\"><span class=\"muted\">{Html(label)}</span><b>{Html(value)}</b></div>");

    /// <summary>Escape mínimo de HTML (o arquivo é UTF-8, então acentos ficam legíveis).</summary>
    public static string Html(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);
}

/// <summary>Exporta pastas (até 3 níveis) e maiores arquivos em CSV (separador ";", UTF-8 com BOM, abre no Excel).</summary>
public static class CsvReportExporter
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static void Export(ScanResult result, string filePath, int maxFolderDepth = 3)
    {
        var sb = new StringBuilder(1024 * 1024);
        sb.AppendLine("Tipo;Caminho;Tamanho (bytes);Tamanho;Arquivos;% da pasta-pai;Modificado;Categoria");

        var stack = new Stack<(ScanNode Node, int Depth)>();
        stack.Push((result.Root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            AppendRow(sb, "Pasta", node, string.Empty);
            if (depth >= maxFolderDepth)
            {
                continue;
            }

            // Empilha em ordem inversa para sair do maior para o menor.
            var dirs = node.Children.Where(c => c.IsDirectory).ToList();
            for (int i = dirs.Count - 1; i >= 0; i--)
            {
                stack.Push((dirs[i], depth + 1));
            }
        }

        foreach (var file in result.LargestFiles)
        {
            AppendRow(sb, "Arquivo", file, FileCategoryInfo.DisplayName(file.Category));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
    }

    private static void AppendRow(StringBuilder sb, string type, ScanNode node, string category)
    {
        sb.Append(type).Append(';')
            .Append(Escape(node.FullPath)).Append(';')
            .Append(node.Size.ToString(CultureInfo.InvariantCulture)).Append(';')
            .Append(Escape(ByteSize.Format(node.Size))).Append(';')
            .Append(node.FileCount.ToString(CultureInfo.InvariantCulture)).Append(';')
            .Append((node.FractionOfParent * 100).ToString("0.##", PtBr)).Append(';')
            .Append(node.LastModified.ToString("dd/MM/yyyy HH:mm", PtBr)).Append(';')
            .Append(Escape(category))
            .AppendLine();
    }

    public static string Escape(string value)
    {
        if (value.IndexOfAny([';', '"', '\n', '\r']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
