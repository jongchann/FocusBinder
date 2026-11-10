using System.Windows;
using System.Windows.Controls;
using FocusBind.Core.Profiles;

namespace FocusBind.App;

internal sealed class ProfileSettingsWindow : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _path = new();
    private readonly TextBox _executable = new();
    private readonly TextBox _title = new();
    private readonly TextBox _class = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    internal string ProfileName => _name.Text.Trim();
    internal WindowMatcher Matcher => new() { ExecutablePath = Value(_path), ExecutableName = Value(_executable),
        TitlePattern = Value(_title), WindowClass = Value(_class) };

    internal ProfileSettingsWindow(Profile profile, WindowIdentity target)
    {
        Title = "프로필 창 조건"; Width = 600; Height = 640; MinHeight = 520; MinWidth = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "프로필 창 조건", FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "입력한 조건을 모두 만족하는 창에 적용합니다. 제목은 정규식입니다. 실행 파일 경로나 이름 중 하나는 필수입니다.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        Add("프로필 이름", _name, profile.Name);
        Add("실행 파일 절대 경로 (선택)", _path, profile.Window.ExecutablePath);
        Add("실행 파일 이름 (예: Langrisser.exe)", _executable, profile.Window.ExecutableName);
        Add("제목 패턴 · 정규식 (선택)", _title, profile.Window.TitlePattern);
        Add("창 클래스 (선택)", _class, profile.Window.WindowClass);
        panel.Children.Add(_status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var check = new Button { Content = "현재 대상과 비교", Margin = new Thickness(0, 0, 8, 8) };
        check.Click += (_, _) => _status.Text = ValidateDraft() ?
            (ProfileResolver.Matches(Matcher, target) ? "현재 대상 창과 일치합니다." : "현재 대상 창과 일치하지 않습니다. 다른 창용 조건으로 저장할 수 있습니다.") : _status.Text;
        var save = new Button { Content = "적용", Margin = new Thickness(0, 0, 8, 8) };
        save.Click += (_, _) => { if (ValidateDraft()) DialogResult = true; };
        var cancel = new Button { Content = "취소", IsCancel = true, Margin = new Thickness(0, 0, 0, 8) };
        buttons.Children.Add(check); buttons.Children.Add(save); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        void Add(string label, TextBox box, string? value)
        {
            panel.Children.Add(new TextBlock { Text = label });
            box.Text = value ?? ""; box.Margin = new Thickness(0, 4, 0, 12);
            panel.Children.Add(box);
        }
    }
    private bool ValidateDraft()
    {
        var errors = ProfileValidator.ValidateWindowMatcher(Matcher).ToList();
        if (ProfileName.Length is < 1 or > 200) errors.Insert(0, "프로필 이름은 1~200자여야 합니다.");
        _status.Text = string.Join(Environment.NewLine, errors);
        return errors.Count == 0;
    }
    private static string? Value(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
}
