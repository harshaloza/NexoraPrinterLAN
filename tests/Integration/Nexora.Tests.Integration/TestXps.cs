using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Xps.Packaging;

namespace Nexora.Tests.Integration;

/// <summary>Creates small XPS documents (as the Nexora driver would deliver) with WPF, on an STA thread.</summary>
public static class TestXps
{
    public static readonly Size A4 = new(210 / 25.4 * 96, 297 / 25.4 * 96);
    public static readonly Size A3Landscape = new(420 / 25.4 * 96, 297 / 25.4 * 96);

    public static string Create(string path, int pages, Size pageSize, string title = "Nexora XPS test")
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var fixedDoc = new FixedDocument();
                fixedDoc.DocumentPaginator.PageSize = pageSize;
                for (int i = 0; i < pages; i++)
                {
                    var page = new FixedPage { Width = pageSize.Width, Height = pageSize.Height, Background = Brushes.White };
                    page.Children.Add(new TextBlock { Text = $"{title} - page {i + 1} of {pages}", FontSize = 28, Margin = new Thickness(72) });
                    var frame = new System.Windows.Shapes.Rectangle
                    {
                        Width = pageSize.Width - 96, Height = pageSize.Height - 96, Stroke = Brushes.Black, StrokeThickness = 1,
                    };
                    FixedPage.SetLeft(frame, 48);
                    FixedPage.SetTop(frame, 48);
                    page.Children.Add(frame);
                    var content = new PageContent();
                    ((IAddChild)content).AddChild(page);
                    fixedDoc.Pages.Add(content);
                }
                if (File.Exists(path)) File.Delete(path);
                using var xps = new XpsDocument(path, FileAccess.ReadWrite);
                XpsDocument.CreateXpsDocumentWriter(xps).Write(fixedDoc);
            }
            catch (Exception e) { error = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
        return path;
    }
}
