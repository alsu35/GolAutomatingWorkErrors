using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xceed.Words.NET;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.SKCharts;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using LiveChartsCore.SkiaSharpView.Avalonia;


namespace AutomatingWorkErrors;

public partial class MainWindow : Window
{
    private string? warningsFilePath;
    private string? gfmFilePath;
    private TextBlock? statusBlock;

    public MainWindow()
    {
        InitializeComponent();

        OneBtn.Click += SelectWarningsFile;
        TwoBtn.Click += SelectGFMFile;
        TotalBtn.Click += GenerateReport;

        statusBlock = this.FindControl<TextBlock>("StatusText");
    }

    private async void SelectWarningsFile(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            AllowMultiple = false,
            Filters = { new FileDialogFilter() { Name = "Excel", Extensions = { "xlsx" } } }
        };
        var result = await dialog.ShowAsync(this);
        if (result != null && result.Length > 0)
        {
            warningsFilePath = result[0];
            SetStatus("✅ Загрузили: Реестр предупреждений");
        }
    }

    private async void SelectGFMFile(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            AllowMultiple = false,
            Filters = { new FileDialogFilter() { Name = "Excel", Extensions = { "xlsx" } } }
        };
        var result = await dialog.ShowAsync(this);
        if (result != null && result.Length > 0)
        {
            gfmFilePath = result[0];
            SetStatus("✅ Загрузили: График ГФМ");
        }
    }

    private void GenerateReport(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(warningsFilePath) || string.IsNullOrEmpty(gfmFilePath))
        {
            SetStatus("❌ Не выбраны оба файла.");
            return;
        }

        var startStr = StartSheetBox.Text?.Trim();
        var endStr = EndSheetBox.Text?.Trim();

        if (!DateTime.TryParse(startStr, out var startDate) || !DateTime.TryParse(endStr, out var endDate))
        {
            SetStatus("❌ Укажите корректные даты в формате: 04.06.2025");
            return;
        }

        try
        {
            var selectedSheets = GetRelevantSheets(warningsFilePath, startDate, endDate);
            var warnings = ExtractWarnings(selectedSheets);
            var brigadeMap = MapBrigades(warnings, gfmFilePath);
            var grouped = GroupWarnings(warnings, brigadeMap);

            var chartPath = CreateHistogram(grouped);
            CreateWordReport(grouped, chartPath);

            SetStatus("✅ Отчёт успешно сформирован: Итог.docx");
        }
        catch (Exception ex)
        {
            SetStatus($"❌ Ошибка: {ex.Message}");
        }
    }

    private List<IXLWorksheet> GetRelevantSheets(string path, DateTime start, DateTime end)
    {
        var book = new XLWorkbook(path);
        var relevant = new List<IXLWorksheet>();

        foreach (var ws in book.Worksheets)
        {
            if (DateTime.TryParseExact(ws.Name.Substring(0, 10), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sheetDate))
            {
                if (sheetDate >= start && sheetDate <= end)
                {
                    relevant.Add(ws);
                }
            }
        }
        return relevant;
    }

    private List<string> ExtractWarnings(List<IXLWorksheet> sheets)
    {
        var list = new List<string>();
        foreach (var ws in sheets)
        {
            var contractor = ws.Cell("H3").GetString();
            if (!contractor.Contains("Гольфстрим", StringComparison.OrdinalIgnoreCase)) continue;

            var wellNumber = ws.Cell("B3").GetString();
            if (!string.IsNullOrWhiteSpace(wellNumber))
                list.Add(wellNumber);
        }
        return list;
    }

    private Dictionary<string, string> MapBrigades(List<string> wells, string gfmPath)
    {
        var wb = new XLWorkbook(gfmPath);
        var ws = wb.Worksheet(1);
        var map = new Dictionary<string, string>();

        foreach (var cell in ws.CellsUsed())
        {
            var value = cell.GetString();
            foreach (var well in wells)
            {
                if (value.Contains(well))
                {
                    var left = ws.Cell(cell.Address.RowNumber, cell.Address.ColumnNumber - 1).GetString();
                    if (left.StartsWith("Бр.", StringComparison.OrdinalIgnoreCase))
                        map[well] = left.Replace("Бр.", "").Trim();
                }
            }
        }
        return map;
    }

    private Dictionary<string, int> GroupWarnings(List<string> wells, Dictionary<string, string> brigadeMap)
    {
        var result = new Dictionary<string, int>();
        foreach (var well in wells)
        {
            if (brigadeMap.TryGetValue(well, out var brigade))
            {
                if (!result.ContainsKey(brigade))
                    result[brigade] = 0;
                result[brigade]++;
            }
        }
        return result;
    }

    private string CreateHistogram(Dictionary<string, int> data)
    {
        var values = data.Values.ToArray();
        var labels = data.Keys.ToArray();

        var series = new ColumnSeries<int>
        {
            Values = values,
            Stroke = new SolidColorPaint(SKColors.Black),
            Fill = new SolidColorPaint(SKColors.SteelBlue)
        };

        var chart = new CartesianChart
        {
            Series = new ISeries[] { series },
            XAxes = new[] { new Axis { Labels = labels, TextSize = 18, Name = "Бригады" } },
            YAxes = new[] { new Axis { TextSize = 18, Name = "Количество" } }
        };

        var width = 600;
        var height = 400;
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        chart.Draw(new SkiaSharpDrawingContext
        {
            Canvas = canvas,
            Width = width,
            Height = height,
            View = null! // Здесь можно подставить реальный контекст, если вы используете внутри Avalonia
        });

        var path = Path.Combine(Directory.GetCurrentDirectory(), "chart.png");
        using var fs = File.OpenWrite(path);
        bitmap.Encode(fs, SKEncodedImageFormat.Png, 100);

        return path;
    }

    private void CreateWordReport(Dictionary<string, int> brigadeCounts, string chartPath)
    {
        var doc = DocX.Create("Итог.docx");
        doc.InsertParagraph("Итоговый отчет по предупреждениям")
            .FontSize(16).Bold().SpacingAfter(20);

        var table = doc.AddTable(brigadeCounts.Count + 1, 2);
        table.Design = Xceed.Document.NET.TableDesign.ColorfulListAccent1;
        table.Rows[0].Cells[0].Paragraphs[0].Append("Бригада");
        table.Rows[0].Cells[1].Paragraphs[0].Append("Количество предупреждений");

        int i = 1;
        foreach (var item in brigadeCounts.OrderBy(x => x.Key))
        {
            table.Rows[i].Cells[0].Paragraphs[0].Append(item.Key);
            table.Rows[i].Cells[1].Paragraphs[0].Append(item.Value.ToString());
            i++;
        }

        doc.InsertTable(table);

        if (File.Exists(chartPath))
        {
            doc.InsertParagraph("\nГистограмма:").SpacingBefore(20);
            using var fs = new FileStream(chartPath, FileMode.Open, FileAccess.Read);
            var img = doc.AddImage(fs);
            var picture = img.CreatePicture();
            picture.Width = 500;
            picture.Height = 300;
            doc.InsertParagraph().AppendPicture(picture);
        }

        doc.Save();
    }

    private void SetStatus(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (statusBlock != null)
                statusBlock.Text = message;
        });
    }
}
