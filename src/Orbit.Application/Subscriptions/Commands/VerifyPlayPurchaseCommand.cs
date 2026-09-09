using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions.Commands;

public record VerifyPlayPurchaseCommand(Guid UserId, string ProductId, string PurchaseToken) : IRequest<Result<PlayVerifyResponse>>;

public partial class VerifyPlayPurchaseCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IPlayBillingService playBilling,
    IPlayReferralCouponConsumer referralCouponConsumer,
    IOptions<GooglePlaySettings> playSettings,
    ILogger<VerifyPlayPurchaseCommandHandler> logger) : IRequestHandler<VerifyPlayPurchaseCommand, Result<PlayVerifyResponse>>
{
    private readonly GooglePlaySettings _settings = playSettings.Value;

    public async Task<Result<PlayVerifyResponse>> Handle(VerifyPlayPurchaseCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: cancellationToken);
        if (user is null)
            return Result.Failure<PlayVerifyResponse>(ErrorMessages.UserNotFound);

        PlaySubscriptionState? state;
        try
        {
            state = await playBilling.VerifyAsync(request.ProductId, request.PurchaseToken, cancellationToken);
        }
        catch (BillingProviderException ex)
        {
            LogVerifyError(logger, ex, request.UserId);
            return Result.Failure<PlayVerifyResponse>(ErrorMessages.PaymentServiceUnavailable);
        }

        if (state is null || !state.GrantsOrbitPro(_settings))
        {
            LogPurchaseNotActive(logger, request.UserId);
            return Result.Failure<PlayVerifyResponse>(ErrorMessages.PlayPurchaseNotActive);
        }

        if (!Guid.TryParse(state.ObfuscatedAccountId, out var purchaseAccountId) || purchaseAccountId != request.UserId)
        {
            LogAccountMismatch(logger, request.UserId);
            return Result.Failure<PlayVerifyResponse>(ErrorMessages.PlayPurchaseAccountMismatch);
        }

        if (!state.IsAcknowledged)
        {
            try
            {
                await playBilling.AcknowledgeAsync(state.ProductId, request.PurchaseToken, cancellationToken);
            }
            catch (BillingProviderException ex)
            {
                LogAcknowledgeError(logger, ex, request.UserId);
            }
        }

        var consumedCouponId = referralCouponConsumer.ConsumeOnNewPurchase(user, state, request.PurchaseToken);

        if (StripeCoversLaterPeriod(user, state))
        {
            user.LinkPlayPurchaseToken(request.PurchaseToken);
            var linkResult = await SavePlayTokenAsync(request, user, cancellationToken);
            if (linkResult.IsSuccess)
            {
                await CancelConsumedCouponAsync(request.UserId, consumedCouponId, cancellationToken);
                LogStripeCoversLaterPeriod(logger, request.UserId);
            }
            return linkResult;
        }

        user.SetPlaySubscription(request.PurchaseToken, state.ExpiresAt, state.Interval, state.IsInGracePeriod);
        var verifyResult = await SavePlayTokenAsync(request, user, cancellationToken);
        if (verifyResult.IsSuccess)
        {
            await CancelConsumedCouponAsync(request.UserId, consumedCouponId, cancellationToken);
            LogPlayPurchaseVerified(logger, request.UserId, state.ExpiresAt);
        }
        return verifyResult;
    }

    private async Task CancelConsumedCouponAsync(Guid userId, string? couponId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(couponId))
            await referralCouponConsumer.CancelConsumedCouponAsync(userId, couponId, cancellationToken);
    }

    private async Task<Result<PlayVerifyResponse>> SavePlayTokenAsync(
        VerifyPlayPurchaseCommand request, User user, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return BuildResponse(user);
        }
        catch (DbUpdateException)
        {
            if (await userRepository.AnyIgnoringFiltersAsync(
                u => u.PlayPurchaseToken == request.PurchaseToken && u.Id != request.UserId, cancellationToken))
            {
                LogAccountMismatch(logger, request.UserId);
                return Result.Failure<PlayVerifyResponse>(
                    ErrorMessages.PlayPurchaseAccountMismatch);
            }
            throw;
        }
    }

    private static bool StripeCoversLaterPeriod(User user, PlaySubscriptionState state) =>
        user.SubscriptionSource == SubscriptionSource.Stripe
        && user.IsPro
        && user.PlanExpiresAt.HasValue
        && user.PlanExpiresAt.Value > state.ExpiresAt;

    private static Result<PlayVerifyResponse> BuildResponse(User user) =>
        Result.Success(new PlayVerifyResponse(
            user.HasProAccess,
            user.SubscriptionSource.ToApiValue(),
            user.SubscriptionInterval?.ToString().ToLowerInvariant(),
            user.PlanExpiresAt));

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Play purchase verify failed for user {UserId}")]
    private static partial void LogVerifyError(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Play purchase not active for user {UserId}")]
    private static partial void LogPurchaseNotActive(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Active Stripe subscription covers a later period; skipping Play grant for user {UserId}")]
    private static partial void LogStripeCoversLaterPeriod(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Failed to acknowledge Play purchase for user {UserId}")]
    private static partial void LogAcknowledgeError(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Play purchase verified for user {UserId}, expires {Expires}")]
    private static partial void LogPlayPurchaseVerified(ILogger logger, Guid userId, DateTime expires);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Play purchase account mismatch for user {UserId}")]
    private static partial void LogAccountMismatch(ILogger logger, Guid userId);
}
