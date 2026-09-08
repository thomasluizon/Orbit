using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Calendar.Commands;

public record DismissCalendarSuggestionCommand(Guid UserId, Guid SuggestionId) : IRequest<Result>;

public class DismissCalendarSuggestionCommandHandler(
    IGenericRepository<GoogleCalendarSyncSuggestion> suggestionRepository,
    IPayGateService payGate,
    IUnitOfWork unitOfWork) : IRequestHandler<DismissCalendarSuggestionCommand, Result>
{
    public async Task<Result> Handle(DismissCalendarSuggestionCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanManageCalendar(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck;

        var suggestion = await suggestionRepository.FindOneTrackedAsync(
            s => s.Id == request.SuggestionId && s.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (suggestion is null)
            return Result.Failure(ErrorMessages.SuggestionNotFound);

#pragma warning disable ORBIT0004
        suggestion.MarkDismissed(DateTime.UtcNow);
#pragma warning restore ORBIT0004
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
