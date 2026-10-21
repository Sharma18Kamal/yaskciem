using CloudEntitle.Desktop.Services;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.Desktop.ViewModels;

public sealed partial class OrganizationViewModel
{
    private readonly ISharedMonitoringApi? _sharedMonitoringApi;
    private IReadOnlyList<SharedNotificationRuleDto> _notificationRules = Array.Empty<SharedNotificationRuleDto>();
    private IReadOnlyList<SharedDriftEventDto> _driftEvents = Array.Empty<SharedDriftEventDto>();
    private IReadOnlyList<SharedDeliveryDto> _notificationDeliveries = Array.Empty<SharedDeliveryDto>();
    private IReadOnlyList<SharedDeliveryAttemptDto> _deliveryAttempts = Array.Empty<SharedDeliveryAttemptDto>();
    private SharedNotificationRuleDto? _selectedNotificationRule;
    private SharedDeliveryDto? _selectedDelivery;
    private SharedDriftEventDto? _selectedOperationalEvent;
    private IReadOnlyList<ConnectorHealthDto> _connectorHealth = Array.Empty<ConnectorHealthDto>();
    private string _connectorKind = "SignedWebhook", _operationalTypes = "", _ticketChannel = "jira";
    private bool _automaticTickets;
    private AsyncRelayCommand? _requestTicketCommand;
    public AsyncRelayCommand RequestOperationalTicketCommand => _requestTicketCommand ??= new(RequestOperationalTicketAsync);
    public IReadOnlyList<string> ConnectorKinds { get; } = new[] { "SignedWebhook", "Jira" };
    public IReadOnlyList<ConnectorHealthDto> ConnectorHealth { get => _connectorHealth; private set => SetProperty(ref _connectorHealth, value); }
    public string NotificationConnectorKind { get => _connectorKind; set => SetProperty(ref _connectorKind, value); }
    public string OperationalTypes { get => _operationalTypes; set => SetProperty(ref _operationalTypes, value); }
    public bool AllowAutomaticTickets { get => _automaticTickets; set => SetProperty(ref _automaticTickets, value); }
    public string TicketChannel { get => _ticketChannel; set => SetProperty(ref _ticketChannel, value); }
    public SharedDriftEventDto? SelectedOperationalEvent { get => _selectedOperationalEvent; set { if (SetProperty(ref _selectedOperationalEvent, value)) OnPropertyChanged(nameof(CanRequestOperationalTicket)); } }
    public bool CanRequestOperationalTicket => CanReadMonitoring && _sharedMonitoringApi is ISharedOperationalApi
        && IsAllowed(OrganizationAction.ManageRemediations) && SelectedOperationalEvent is not null;
    private string _monitoringStatus = "Central drift and alerts require shared organization mode.";
    private string _notificationRuleName = "Important access and risk changes";
    private string _notificationChannel = "security";
    private SharedAlertSeverity _alertMinimumSeverity = SharedAlertSeverity.High;
    private bool _notificationEnabled = true, _alertOnFindings = true, _alertOnAccess = true,
        _alertOnAdded = true, _alertOnRemoved = true, _alertOnChanged = true;

    public AsyncRelayCommand RefreshMonitoringCommand { get; }
    public AsyncRelayCommand SaveNotificationRuleCommand { get; }
    public AsyncRelayCommand NewNotificationRuleCommand { get; }
    public AsyncRelayCommand LoadDeliveryAttemptsCommand { get; }
    public bool CanReadMonitoring => _sharedMonitoringApi is not null && _identity is not null && SelectedOrganization is not null
        && IsAllowed(OrganizationAction.ViewInventory) && IsAllowed(OrganizationAction.ViewFindings);
    public bool CanManageNotificationRules => CanReadMonitoring && IsAllowed(OrganizationAction.ManagePolicies);
    public IReadOnlyList<SharedNotificationRuleDto> NotificationRules { get => _notificationRules; private set => SetProperty(ref _notificationRules, value); }
    public IReadOnlyList<SharedDriftEventDto> DriftEvents { get => _driftEvents; private set => SetProperty(ref _driftEvents, value); }
    public IReadOnlyList<SharedDeliveryDto> NotificationDeliveries { get => _notificationDeliveries; private set => SetProperty(ref _notificationDeliveries, value); }
    public IReadOnlyList<SharedDeliveryAttemptDto> DeliveryAttempts { get => _deliveryAttempts; private set => SetProperty(ref _deliveryAttempts, value); }
    public string MonitoringStatus { get => _monitoringStatus; private set => SetProperty(ref _monitoringStatus, value); }
    public IReadOnlyList<SharedAlertSeverity> AlertSeverities { get; } = Enum.GetValues<SharedAlertSeverity>();
    public string NotificationRuleName { get => _notificationRuleName; set => SetProperty(ref _notificationRuleName, value); }
    public string NotificationChannel { get => _notificationChannel; set => SetProperty(ref _notificationChannel, value); }
    public SharedAlertSeverity AlertMinimumSeverity { get => _alertMinimumSeverity; set => SetProperty(ref _alertMinimumSeverity, value); }
    public bool NotificationEnabled { get => _notificationEnabled; set => SetProperty(ref _notificationEnabled, value); }
    public bool AlertOnFindings { get => _alertOnFindings; set => SetProperty(ref _alertOnFindings, value); }
    public bool AlertOnAccess { get => _alertOnAccess; set => SetProperty(ref _alertOnAccess, value); }
    public bool AlertOnAdded { get => _alertOnAdded; set => SetProperty(ref _alertOnAdded, value); }
    public bool AlertOnRemoved { get => _alertOnRemoved; set => SetProperty(ref _alertOnRemoved, value); }
    public bool AlertOnChanged { get => _alertOnChanged; set => SetProperty(ref _alertOnChanged, value); }
    public SharedDeliveryDto? SelectedDelivery
    {
        get => _selectedDelivery;
        set { if (SetProperty(ref _selectedDelivery, value)) DeliveryAttempts = Array.Empty<SharedDeliveryAttemptDto>(); }
    }
    public SharedNotificationRuleDto? SelectedNotificationRule
    {
        get => _selectedNotificationRule;
        set
        {
            if (!SetProperty(ref _selectedNotificationRule, value) || value is null) return;
            NotificationRuleName = value.Name;
            NotificationChannel = value.Channel;
            AlertMinimumSeverity = value.MinimumSeverity;
            NotificationEnabled = value.Enabled;
            AlertOnFindings = value.Findings;
            AlertOnAccess = value.EffectiveAccess;
            AlertOnAdded = value.Added;
            AlertOnRemoved = value.Removed;
            AlertOnChanged = value.Changed;
            NotificationConnectorKind = value.ConnectorKind;
            AllowAutomaticTickets = value.AllowAutomaticTickets;
            OperationalTypes = string.Join(", ", value.EventTypes);
        }
    }

    private void ResetNotificationEditor()
    {
        NotificationRuleName = "Important access and risk changes";
        NotificationChannel = "security";
        NotificationConnectorKind = "SignedWebhook";
        AllowAutomaticTickets = false;
        OperationalTypes = "";
        AlertMinimumSeverity = SharedAlertSeverity.High;
        NotificationEnabled = AlertOnFindings = AlertOnAccess = AlertOnAdded = AlertOnRemoved = AlertOnChanged = true;
    }

    private void ResetMonitoring()
    {
        NotificationRules = Array.Empty<SharedNotificationRuleDto>();
        DriftEvents = Array.Empty<SharedDriftEventDto>();
        NotificationDeliveries = Array.Empty<SharedDeliveryDto>();
        DeliveryAttempts = Array.Empty<SharedDeliveryAttemptDto>();
        SelectedNotificationRule = null;
        SelectedDelivery = null;
        SelectedOperationalEvent = null;
        ConnectorHealth = Array.Empty<ConnectorHealthDto>();
        ResetNotificationEditor();
        MonitoringStatus = "Central drift and alerts require a shared organization and inventory/finding permissions.";
    }

    public async Task RefreshMonitoringAsync()
    {
        if (!CanReadMonitoring || _sharedMonitoringApi is null || SelectedOrganization is null)
        {
            ResetMonitoring();
            return;
        }
        var organization = SelectedOrganization;
        var identity = _identity;
        try
        {
            var rules = await _sharedMonitoringApi.GetNotificationRulesAsync(organization.Id);
            var events = await _sharedMonitoringApi.GetDriftEventsAsync(organization.Id);
            var deliveries = await _sharedMonitoringApi.GetNotificationDeliveriesAsync(organization.Id);
            var health = _sharedMonitoringApi is ISharedOperationalApi operations
                ? await operations.GetConnectorHealthAsync(organization.Id) : Array.Empty<ConnectorHealthDto>();
            if (SelectedOrganization != organization || _identity != identity) return;
            NotificationRules = rules;
            DriftEvents = events;
            NotificationDeliveries = deliveries;
            ConnectorHealth = health;
            SelectedOperationalEvent = null;
            SelectedDelivery = null;
            MonitoringStatus = $"{events.Count} recent drift event(s); {deliveries.Count(item => item.Status == "Failed")} failed and {deliveries.Count(item => item.Status is "Pending" or "Sending")} pending/sending deliveries. First complete scans establish a baseline.";
        }
        catch (Exception exception)
        {
            if (SelectedOrganization != organization || _identity != identity) return;
            ResetMonitoring();
            MonitoringStatus = $"Central monitoring refresh failed: {exception.Message}";
        }
    }

    public async Task SaveNotificationRuleAsync()
    {
        if (!CanManageNotificationRules || _sharedMonitoringApi is null || SelectedOrganization is null)
        {
            MonitoringStatus = "ManagePolicies permission is required to save notification rules.";
            return;
        }
        var organization = SelectedOrganization;
        var identity = _identity;
        try
        {
            var rule = new SharedNotificationRuleDto(SelectedNotificationRule?.RuleId ?? Guid.NewGuid(),
                NotificationRuleName.Trim(), NotificationEnabled, AlertMinimumSeverity, AlertOnFindings, AlertOnAccess,
                AlertOnAdded, AlertOnRemoved, AlertOnChanged, NotificationChannel.Trim())
            {
                ConnectorKind = NotificationConnectorKind,
                AllowAutomaticTickets = AllowAutomaticTickets,
                EventTypes = OperationalTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray()
            };
            var saved = await _sharedMonitoringApi.SaveNotificationRuleAsync(organization.Id, rule);
            if (SelectedOrganization != organization || _identity != identity) return;
            await RefreshMonitoringAsync();
            SelectedNotificationRule = NotificationRules.FirstOrDefault(item => item.RuleId == saved.RuleId);
        }
        catch (Exception exception)
        {
            if (SelectedOrganization == organization && _identity == identity)
                MonitoringStatus = $"Notification rule save failed: {exception.Message}";
        }
    }

    public async Task RequestOperationalTicketAsync()
    {
        if (!CanRequestOperationalTicket || SelectedOrganization is null || SelectedOperationalEvent is null
            || _sharedMonitoringApi is not ISharedOperationalApi operations)
        {
            MonitoringStatus = "Select an event; shared mode and ManageRemediations are required to request a ticket.";
            return;
        }
        var organization = SelectedOrganization;
        var identity = _identity;
        var eventId = SelectedOperationalEvent.EventId;
        try
        {
            var status = await operations.RequestOperationalTicketAsync(organization.Id, eventId, TicketChannel.Trim());
            if (SelectedOrganization != organization || _identity != identity) return;
            await RefreshMonitoringAsync();
            MonitoringStatus = $"Jira delivery {status.Delivery.DeliveryId:D}: {status.Delivery.Status}. Repeated requests are deduplicated; dead letters require reconciliation.";
        }
        catch (Exception exception)
        {
            if (SelectedOrganization == organization && _identity == identity)
                MonitoringStatus = $"Ticket request failed: {exception.Message}";
        }
    }

    public async Task LoadDeliveryAttemptsAsync()
    {
        if (!CanReadMonitoring || _sharedMonitoringApi is null || SelectedOrganization is null || SelectedDelivery is null)
        {
            MonitoringStatus = "Select a notification delivery to inspect its attempts.";
            return;
        }
        var organization = SelectedOrganization;
        var identity = _identity;
        var selected = SelectedDelivery;
        try
        {
            var status = await _sharedMonitoringApi.GetNotificationDeliveryStatusAsync(organization.Id, selected.DeliveryId);
            if (SelectedOrganization == organization && _identity == identity && SelectedDelivery == selected)
                DeliveryAttempts = status.Attempts;
        }
        catch (Exception exception)
        {
            if (SelectedOrganization == organization && _identity == identity)
                MonitoringStatus = $"Delivery status read failed: {exception.Message}";
        }
    }
}
