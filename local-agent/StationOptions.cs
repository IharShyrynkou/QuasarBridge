namespace LocalAgent;

public sealed class StationOptions
{
    public const string SectionName = "Station";

    public string Host { get; set; } = "192.168.0.195";
    public string User { get; set; } = "root";
    public string KeyPath { get; set; } = @"C:\Users\Admin\Documents\ssh\id_rsa";
    public string Phrase { get; set; } = "Хозяин, всё готово.";
    public int Card { get; set; }
    public int Device { get; set; }
}
