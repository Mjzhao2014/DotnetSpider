namespace DotnetSpider.Robots;

/// <summary>
/// Options used to configure robots.txt usage.
/// </summary>
public class RobotsTxtOptions
{
    /// <summary>
    /// User agent string to look up in robots.txt. If not set, '*' will be used.
    /// </summary>
    public string UserAgent { get; set; } = "*";
}
