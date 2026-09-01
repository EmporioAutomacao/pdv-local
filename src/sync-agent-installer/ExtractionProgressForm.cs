namespace SyncAgent.Installer;

internal sealed class ExtractionProgressForm : Form
{
    public string? PayloadRoot { get; private set; }

    public ExtractionProgressForm()
    {
        Text = "AraraSuite - Instalador";
        Width = 420;
        Height = 140;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;

        var label = new Label
        {
            Text = "Preparando instalador...",
            Dock = DockStyle.Top,
            Height = 32,
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(0, 16, 0, 0)
        };
        var bar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 24,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30
        };

        Controls.Add(bar);
        Controls.Add(label);

        Shown += async (_, _) =>
        {
            PayloadRoot = await Task.Run(EmbeddedPayload.ExtractIfPresent);
            Close();
        };
    }
}
