namespace rabotyagi.DataAccess.Enums;

public enum EOrderHistoryEventType
{
    Created,
    StatusChanged,
    ExecutorAssigned,
    ReportSubmitted,
    ReportApproved,
    ReportReturned,
    ExecutorChanged,
    PriceProposed,
    PriceAccepted,
    PriceRejected,
    WorkAcceptedByClient,
    WorkReturnedByClient
}
