using rabotyagi.DataAccess.Enums;

namespace rabotyagi.API.Features.Order.DTOs;

public record OrderQueueCountDto(EOrderStatus Status, int Count);
