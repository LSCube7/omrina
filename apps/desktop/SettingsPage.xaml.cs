using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Omrina.Protocol;
using Omrina.Server;

namespace Omrina.Desktop;

public sealed partial class SettingsPage : Page
{
    private readonly LoopbackHealthServer _agentServer;
    private readonly DispatcherQueue _dispatcherQueue;
    private string? _pendingSnapshotKey;
    private string? _grantSnapshotKey;
    private bool _subscribed;

    public SettingsPage(LoopbackHealthServer agentServer, DispatcherQueue dispatcherQueue)
    {
        _agentServer = agentServer ?? throw new ArgumentNullException(nameof(agentServer));
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
        Unloaded += SettingsPage_Unloaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs args)
    {
        if (!_subscribed)
        {
            _agentServer.PairingStateChanged += AgentServer_PairingStateChanged;
            _subscribed = true;
        }

        RefreshAuthorizationLists();
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs args)
    {
        if (_subscribed)
        {
            _agentServer.PairingStateChanged -= AgentServer_PairingStateChanged;
            _subscribed = false;
        }
    }

    private void AgentServer_PairingStateChanged()
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            RefreshAuthorizationLists();
            return;
        }

        _dispatcherQueue.TryEnqueue(RefreshAuthorizationLists);
    }

    private void RefreshAuthorizationLists()
    {
        var pending = _agentServer.PendingPairings
            .OrderBy(pairing => pairing.Origin, StringComparer.Ordinal)
            .ThenBy(pairing => pairing.ClientName, StringComparer.Ordinal)
            .ThenBy(pairing => pairing.RequestId, StringComparer.Ordinal)
            .ToArray();
        var grants = _agentServer.ActiveGrants
            .OrderBy(grant => grant.Origin, StringComparer.Ordinal)
            .ThenBy(grant => grant.ClientName, StringComparer.Ordinal)
            .ThenBy(grant => grant.GrantId, StringComparer.Ordinal)
            .ToArray();

        var pendingSnapshotKey = string.Join(
            "\n",
            pending.Select(pairing => $"{pairing.RequestId}|{pairing.Origin}|{pairing.ClientName}|{pairing.ExpiresAt:O}"));
        if (!string.Equals(_pendingSnapshotKey, pendingSnapshotKey, StringComparison.Ordinal))
        {
            _pendingSnapshotKey = pendingSnapshotKey;
            RenderPairings(pending);
        }

        var grantSnapshotKey = string.Join(
            "\n",
            grants.Select(grant => $"{grant.GrantId}|{grant.Origin}|{grant.ClientName}|{grant.CreatedAt:O}|{grant.ExpiresAt:O}"));
        if (!string.Equals(_grantSnapshotKey, grantSnapshotKey, StringComparison.Ordinal))
        {
            _grantSnapshotKey = grantSnapshotKey;
            RenderGrants(grants);
        }
    }

    private void RenderPairings(IReadOnlyList<PendingPairing> pairings)
    {
        PairingListPanel.Children.Clear();
        if (pairings.Count == 0)
        {
            PairingListPanel.Children.Add(new TextBlock
            {
                Text = "目前没有待处理的网站申请。",
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var pairing in pairings)
        {
            var card = CreateCard();
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(pairing.ClientName)
                    ? "网站请求配对"
                    : $"{pairing.ClientName} 请求配对",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = pairing.Origin,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
            content.Children.Add(new TextBlock
            {
                Text = $"申请功能：生成模板、上传或扫描答题纸、识别、评分、人工复核、导出 JSON / CSV。申请将在 {pairing.ExpiresAt.ToLocalTime():g} 过期。",
                TextWrapping = TextWrapping.Wrap
            });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var approve = new Button { Content = "允许", Tag = pairing.RequestId };
            approve.Click += ApprovePairing_Click;
            var reject = new Button { Content = "拒绝", Tag = pairing.RequestId };
            reject.Click += RejectPairing_Click;
            actions.Children.Add(approve);
            actions.Children.Add(reject);
            content.Children.Add(actions);
            card.Child = content;
            PairingListPanel.Children.Add(card);
        }
    }

    private void RenderGrants(IReadOnlyList<GrantSummary> grants)
    {
        GrantListPanel.Children.Clear();
        if (grants.Count == 0)
        {
            GrantListPanel.Children.Add(new TextBlock
            {
                Text = "目前没有有效的网站授权。",
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var grant in grants)
        {
            var card = CreateCard();
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(grant.ClientName) ? "已授权网站" : grant.ClientName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = grant.Origin,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
            content.Children.Add(new TextBlock
            {
                Text = $"已授权功能：生成模板、上传或扫描答题纸、识别、评分、人工复核、导出 JSON / CSV。授权于 {grant.CreatedAt.ToLocalTime():g}，有效期至 {grant.ExpiresAt.ToLocalTime():g}。可随时撤销。",
                TextWrapping = TextWrapping.Wrap
            });

            var revoke = new Button { Content = "撤销授权", Tag = grant.GrantId };
            revoke.Click += RevokeGrant_Click;
            content.Children.Add(revoke);
            card.Child = content;
            GrantListPanel.Children.Add(card);
        }
    }

    private static Border CreateCard() => new()
    {
        Padding = new Thickness(12),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1)
    };

    private async void ApprovePairing_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string requestId })
        {
            return;
        }

        try
        {
            var origin = _agentServer.PendingPairings
                .FirstOrDefault(pairing => string.Equals(pairing.RequestId, requestId, StringComparison.Ordinal))
                ?.Origin;
            var code = _agentServer.ApprovePairing(requestId);
            AuthorizationStatusText.Text = "网站申请已允许。请将本地显示的一次性配对码输入到对应网站。";
            var codeBox = new TextBox
            {
                Text = code,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MaxHeight = 96,
                FontSize = 22
            };
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = $"网站：{origin ?? "（申请已过期）"}",
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
            content.Children.Add(new TextBlock
            {
                Text = "请在对应网站的配对页面输入此代码。代码只在本窗口显示一次，可在代码框中选择并复制。",
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(codeBox);
            var dialog = new ContentDialog
            {
                Title = "一次性配对码",
                Content = content,
                CloseButtonText = "关闭",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
            codeBox.Text = string.Empty;
        }
        catch (Exception exception)
        {
            AuthorizationStatusText.Text =
                "无法允许此申请。它可能已过期或已被处理，请刷新网站后重试。 "
                + $"调试信息：模块 website-pairing，错误类型 PAIRING_APPROVAL_FAILED，"
                + $"异常 {exception.GetType().Name}（0x{exception.HResult:X8}）。";
        }
    }

    private void RejectPairing_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string requestId })
        {
            return;
        }

        var rejected = _agentServer.RejectPairing(requestId);
        AuthorizationStatusText.Text = rejected
            ? "网站申请已拒绝。"
            : "此申请已过期或已被处理。";
    }

    private void RevokeGrant_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string grantId })
        {
            return;
        }

        var revoked = _agentServer.RevokeGrant(grantId);
        AuthorizationStatusText.Text = revoked
            ? "网站授权已撤销。"
            : "此授权已过期或已撤销。";
    }
}
