using System.Collections.ObjectModel;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.OrganizationManagement.Models;
using System.Text.Json;
using Serilog;

namespace CloudEntitle.Desktop.ViewModels;

public sealed record ToxicRuleProviderChoice(string DisplayName, CloudProvider? Provider);

public sealed class ToxicCombinationViewModel : WorkspaceViewModel
{
    private readonly IToxicCombinationWorkspaceService _workspace;
    private readonly IDemoDataService _demoData;
    private readonly AuthenticatedOrganizationContext? _organizationContext;
    private string _searchText = string.Empty;
    private string _status = "Toxic-combination analysis uses effective-access evidence from a completed scan.";
    private ToxicCombinationFindingRow? _selectedFinding;
    private IReadOnlyList<ToxicCombinationFindingRow> _findings = Array.Empty<ToxicCombinationFindingRow>();
    private IReadOnlyList<ToxicCombinationEvidenceRow> _evidence = Array.Empty<ToxicCombinationEvidenceRow>();
    private IReadOnlyList<ToxicCombinationRecommendationRow> _recommendations = Array.Empty<ToxicCombinationRecommendationRow>();
    private IReadOnlyList<ToxicCombinationTrustPathRow> _trustPathEvidence = Array.Empty<ToxicCombinationTrustPathRow>();
    private IReadOnlyList<ToxicCombinationRuleAuditRow> _ruleAuditHistory = Array.Empty<ToxicCombinationRuleAuditRow>();
    private IReadOnlyList<ToxicCombinationFindingRow> _previewFindings = Array.Empty<ToxicCombinationFindingRow>();
    private string _newRuleKey = string.Empty, _newRuleName = string.Empty, _newRuleDescription = string.Empty;
    private string _newRuleFirstPattern = string.Empty, _newRuleSecondPattern = string.Empty, _newRuleRecommendation = string.Empty;
    private string _ruleEditorStatus = "Preview a custom rule against the current local inventory before saving it.";
    private ToxicRuleProviderChoice _newRuleProvider = new("AWS", CloudProvider.Aws);
    private ToxicResourceRequirement _newRuleResourceRequirement;
    private RiskSeverity _newRuleSeverity = RiskSeverity.High;
    private int _newRuleRiskScore = 70;
    private bool _newRuleRequireSameResource, _newRuleRequireTrustPath, _newRuleFirstRequiresPrivileged, _newRuleSecondRequiresPrivileged;
    private string? _previewedRuleFingerprint;

    public ToxicCombinationViewModel(IToxicCombinationWorkspaceService workspace, IDemoDataService demoData,
        AuthenticatedOrganizationContext? organizationContext = null)
        : base(demoData)
    {
        _workspace = workspace;
        _demoData = demoData;
        _organizationContext = organizationContext;
        Rules = [];
        ReloadCommand = new AsyncRelayCommand(RefreshAsync);
        SaveRulesCommand = new AsyncRelayCommand(SaveRulesAsync);
        LoadDemoCommand = new AsyncRelayCommand(LoadDemoAsync);
        PreviewNewRuleCommand = new AsyncRelayCommand(PreviewNewRuleAsync);
        AddNewRuleCommand = new AsyncRelayCommand(AddNewRuleAsync);
        _ = RefreshAsync();
    }

    public override string Title => "Toxic Combinations";
    public override string Subtitle => "Review risky combinations of effective permissions, with provider-specific semantics, scope, conditions and source evidence.";

    public ObservableCollection<ToxicCombinationRule> Rules { get; }
    public IReadOnlyList<ToxicCombinationFindingRow> Findings => _findings;
    public IReadOnlyList<ToxicCombinationFindingRow> FilteredFindings => _findings
        .Where(item => string.IsNullOrWhiteSpace(_searchText)
            || $"{item.RuleName} {item.Provider} {item.Identity} {item.Resource} {item.Severity} {item.Evidence}"
                .Contains(_searchText, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    public IReadOnlyList<ToxicCombinationEvidenceRow> FilteredEvidence => _selectedFinding is null
        ? Array.Empty<ToxicCombinationEvidenceRow>()
        : _evidence.Where(item => item.FindingId == _selectedFinding.Id).ToArray();
    public IReadOnlyList<ToxicCombinationRecommendationRow> Recommendations => _recommendations;
    public IReadOnlyList<ToxicCombinationTrustPathRow> TrustPathEvidence => _trustPathEvidence;
    public IReadOnlyList<ToxicCombinationRuleAuditRow> RuleAuditHistory => _ruleAuditHistory;
    public IReadOnlyList<ToxicCombinationFindingRow> PreviewFindings => _previewFindings;
    public IReadOnlyList<ToxicRuleProviderChoice> RuleProviders { get; } =
    [
        new("All providers / cross-cloud", null),
        new("AWS", CloudProvider.Aws),
        new("Azure", CloudProvider.Azure),
        new("GCP", CloudProvider.Gcp)
    ];
    public IReadOnlyList<ToxicResourceRequirement> ResourceRequirements { get; } = Enum.GetValues<ToxicResourceRequirement>();
    public IReadOnlyList<RiskSeverity> RuleSeverities { get; } = Enum.GetValues<RiskSeverity>();
    public string RuleEditorStatus { get => _ruleEditorStatus; private set => SetProperty(ref _ruleEditorStatus, value); }
    public string NewRuleKey { get => _newRuleKey; set => SetDraftProperty(ref _newRuleKey, value); }
    public string NewRuleName { get => _newRuleName; set => SetDraftProperty(ref _newRuleName, value); }
    public string NewRuleDescription { get => _newRuleDescription; set => SetDraftProperty(ref _newRuleDescription, value); }
    public string NewRuleFirstPattern { get => _newRuleFirstPattern; set => SetDraftProperty(ref _newRuleFirstPattern, value); }
    public string NewRuleSecondPattern { get => _newRuleSecondPattern; set => SetDraftProperty(ref _newRuleSecondPattern, value); }
    public string NewRuleRecommendation { get => _newRuleRecommendation; set => SetDraftProperty(ref _newRuleRecommendation, value); }
    public ToxicRuleProviderChoice NewRuleProvider { get => _newRuleProvider; set => SetDraftProperty(ref _newRuleProvider, value); }
    public ToxicResourceRequirement NewRuleResourceRequirement { get => _newRuleResourceRequirement; set => SetDraftProperty(ref _newRuleResourceRequirement, value); }
    public RiskSeverity NewRuleSeverity { get => _newRuleSeverity; set => SetDraftProperty(ref _newRuleSeverity, value); }
    public int NewRuleRiskScore { get => _newRuleRiskScore; set => SetDraftProperty(ref _newRuleRiskScore, value); }
    public bool NewRuleRequireSameResource { get => _newRuleRequireSameResource; set => SetDraftProperty(ref _newRuleRequireSameResource, value); }
    public bool NewRuleRequireTrustPath { get => _newRuleRequireTrustPath; set => SetDraftProperty(ref _newRuleRequireTrustPath, value); }
    public bool NewRuleFirstRequiresPrivileged { get => _newRuleFirstRequiresPrivileged; set => SetDraftProperty(ref _newRuleFirstRequiresPrivileged, value); }
    public bool NewRuleSecondRequiresPrivileged { get => _newRuleSecondRequiresPrivileged; set => SetDraftProperty(ref _newRuleSecondRequiresPrivileged, value); }
    public ToxicCombinationFindingRow? SelectedFinding
    {
        get => _selectedFinding;
        set
        {
            if (!SetProperty(ref _selectedFinding, value)) return;
            OnPropertyChanged(nameof(FilteredEvidence));
        }
    }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            OnPropertyChanged(nameof(FilteredFindings));
        }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public int OpenFindingCount => _findings.Count(item => item.EvaluationState != "Unsupported");
    public int CriticalFindingCount => _findings.Count(item => item.Severity == "Critical");
    public int HighFindingCount => _findings.Count(item => item.Severity == "High");
    public int EnabledRuleCount => Rules.Count(item => item.Enabled);
    public bool HasFindings => _findings.Count > 0;
    public AsyncRelayCommand ReloadCommand { get; }
    public AsyncRelayCommand SaveRulesCommand { get; }
    public AsyncRelayCommand LoadDemoCommand { get; }
    public AsyncRelayCommand PreviewNewRuleCommand { get; }
    public AsyncRelayCommand AddNewRuleCommand { get; }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var rules = await _workspace.GetRulesAsync(CancellationToken.None);
            Rules.Clear();
            foreach (var rule in rules) Rules.Add(rule);
            _findings = await _workspace.GetFindingsAsync(CancellationToken.None);
            _evidence = await _workspace.GetEvidenceAsync(CancellationToken.None);
            _recommendations = await _workspace.GetRecommendationsAsync(CancellationToken.None);
            _trustPathEvidence = await _workspace.GetTrustPathEvidenceAsync(CancellationToken.None);
            _ruleAuditHistory = await _workspace.GetRuleAuditHistoryAsync(CancellationToken.None);
            SelectedFinding = _findings.FirstOrDefault();
            RaiseDashboardProperties();
            Status = _findings.Count == 0
                ? "No saved toxic-combination findings. Run an on-demand cloud scan or load synthetic Demo Mode data."
                : $"Loaded {_findings.Count} persisted finding(s), {_evidence.Count} evidence record(s), and {_recommendations.Count} review-only recommendation(s).";
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Unable to load toxic-combination workspace");
            ErrorMessage = "Unable to load toxic-combination data. Check the local database and application log.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SaveRulesAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var identity = _organizationContext?.Identity;
            await _workspace.SaveRulesAsync(Rules.ToArray(), CancellationToken.None,
                identity?.UserId, _organizationContext?.OrganizationId, identity?.Email);
            Status = "Toxic-combination rules saved. They will be used in subsequent on-demand risk evaluations.";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Unable to save toxic-combination rules");
            ErrorMessage = "Rule changes were not saved. Check pattern syntax, values, and the application log.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task PreviewNewRuleAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var rule = CreateDraftRule();
            _previewFindings = await _workspace.PreviewRuleAsync(rule, CancellationToken.None);
            _previewedRuleFingerprint = Fingerprint(rule);
            OnPropertyChanged(nameof(PreviewFindings));
            RuleEditorStatus = _previewFindings.Count == 0
                ? "Preview completed with no matches in the current local inventory. This does not prove the rule cannot match other identities or resources."
                : $"Preview found {_previewFindings.Count} candidate(s). Findings are potential risks, not confirmed exploitation.";
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Unable to preview toxic-combination rule {RuleKey}", NewRuleKey);
            ErrorMessage = "Rule preview failed. Verify the rule fields and local inventory, then see the application log.";
            _previewedRuleFingerprint = null;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task AddNewRuleAsync()
    {
        ErrorMessage = null;
        try
        {
            var rule = CreateDraftRule();
            if (!string.Equals(_previewedRuleFingerprint, Fingerprint(rule), StringComparison.Ordinal))
                throw new InvalidOperationException("Preview this exact rule definition before saving it.");
            var identity = _organizationContext?.Identity;
            await _workspace.AddRuleAsync(rule, CancellationToken.None, identity?.UserId,
                _organizationContext?.OrganizationId, identity?.Email);
            _previewedRuleFingerprint = null;
            _previewFindings = Array.Empty<ToxicCombinationFindingRow>();
            OnPropertyChanged(nameof(PreviewFindings));
            RuleEditorStatus = $"Rule {rule.Key} was saved as version 1.0; the change was written to audit history.";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Unable to add toxic-combination rule {RuleKey}", NewRuleKey);
            ErrorMessage = exception is InvalidOperationException
                ? exception.Message
                : "The custom rule was not saved. Check its unique key, fields, and the application log.";
        }
    }

    private async Task LoadDemoAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            await _demoData.LoadAsync();
            await _workspace.LoadDemoAsync(CancellationToken.None);
            await RefreshAsync();
            Status = "Loaded synthetic AWS toxic-combination evidence. Demo data is not client telemetry and no cloud endpoint was contacted.";
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Unable to generate synthetic toxic-combination Demo Mode data");
            ErrorMessage = "Unable to load toxic-combination Demo Mode data. Check the local database and application log.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RaiseDashboardProperties()
    {
        OnPropertyChanged(nameof(Findings));
        OnPropertyChanged(nameof(FilteredFindings));
        OnPropertyChanged(nameof(FilteredEvidence));
        OnPropertyChanged(nameof(Recommendations));
        OnPropertyChanged(nameof(OpenFindingCount));
        OnPropertyChanged(nameof(CriticalFindingCount));
        OnPropertyChanged(nameof(HighFindingCount));
        OnPropertyChanged(nameof(EnabledRuleCount));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(TrustPathEvidence));
        OnPropertyChanged(nameof(RuleAuditHistory));
    }

    private bool SetDraftProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName)) return false;
        _previewedRuleFingerprint = null;
        _previewFindings = Array.Empty<ToxicCombinationFindingRow>();
        OnPropertyChanged(nameof(PreviewFindings));
        RuleEditorStatus = "Rule changed. Preview the exact definition again before saving.";
        return true;
    }

    private ToxicCombinationRule CreateDraftRule() => new()
    {
        Key = NewRuleKey,
        Name = NewRuleName,
        Description = NewRuleDescription,
        Provider = NewRuleProvider.Provider,
        FirstPermissionPattern = NewRuleFirstPattern,
        SecondPermissionPattern = NewRuleSecondPattern,
        Recommendation = NewRuleRecommendation,
        ResourceRequirement = NewRuleResourceRequirement,
        Severity = NewRuleSeverity,
        RiskScore = NewRuleRiskScore,
        RequireSameResource = NewRuleRequireSameResource,
        RequireTrustPath = NewRuleRequireTrustPath,
        FirstRequiresPrivilegedAccess = NewRuleFirstRequiresPrivileged,
        SecondRequiresPrivilegedAccess = NewRuleSecondRequiresPrivileged
    };

    private static string Fingerprint(ToxicCombinationRule rule)
        => JsonSerializer.Serialize(new
        {
            rule.Key, rule.Name, rule.Description, rule.Provider, rule.FirstPermissionPattern,
            rule.SecondPermissionPattern, rule.Recommendation, rule.ResourceRequirement, rule.Severity,
            rule.RiskScore, rule.RequireSameResource, rule.RequireTrustPath,
            rule.FirstRequiresPrivilegedAccess, rule.SecondRequiresPrivilegedAccess
        });
}
