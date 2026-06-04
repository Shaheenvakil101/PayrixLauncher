using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PayrixLauncher.Models;

public class WebhookTestCase : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Payload { get; set; } = "";

    private TestStatus _status = TestStatus.Pending;
    public TestStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusLabel)); OnPropertyChanged(nameof(IsPending)); }
    }

    private int? _httpCode;
    public int? HttpCode
    {
        get => _httpCode;
        set { _httpCode = value; OnPropertyChanged(); }
    }

    private long _durationMs;
    public long DurationMs
    {
        get => _durationMs;
        set { _durationMs = value; OnPropertyChanged(); }
    }

    private string _detail = "";
    public string Detail
    {
        get => _detail;
        set { _detail = value; OnPropertyChanged(); }
    }

    public string StatusLabel => Status switch
    {
        TestStatus.Pending  => "—",
        TestStatus.Running  => "…",
        TestStatus.Pass     => "PASS",
        TestStatus.Fail     => "FAIL",
        TestStatus.Skipped  => "SKIP",
        _                   => "?"
    };

    public bool IsPending => Status == TestStatus.Pending;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum TestStatus { Pending, Running, Pass, Fail, Skipped }
