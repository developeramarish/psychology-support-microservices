﻿using BuildingBlocks.Messaging.Events.Auth;
using BuildingBlocks.Messaging.Events.Notification;
using BuildingBlocks.Messaging.Events.Subscription;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;
using Payment.Domain.Events;

namespace Payment.Application.Payments.EventHandlers;

public class SubscriptionPaymentCompletedEventHandler(
    ILogger<SubscriptionPaymentCompletedEventHandler> logger,
    IPublishEndpoint publishEndpoint,
    IRequestClient<GetUserDataRequest> authClient) : INotificationHandler<SubscriptionPaymentDetailCompletedEvent>
{
    public async Task Handle(SubscriptionPaymentDetailCompletedEvent notification, CancellationToken cancellationToken)
    {
        logger.LogInformation("*** Handling SubscriptionPaymentDetailCompletedEvent for SubscriptionId: {SubscriptionId}",
            notification.SubscriptionId);

        var activateSubscriptionEvent = new SubscriptionPaymentSuccessIntegrationEvent(notification.SubscriptionId);

        var sendEmailEvent = new SendEmailIntegrationEvent(notification.PatientEmail,  "Gói đăng ký đã được kích hoạt",
            "Gói đăng ký của bạn đã được kích hoạt thành công.");
        var userDataResponse =
            await authClient.GetResponse<GetUserDataResponse>(new GetUserDataRequest(null, notification.PatientEmail),
                cancellationToken);

        var FCMTokens = userDataResponse.Message.FCMTokens;

        if (FCMTokens.Any())
        {
            var sendMobilePushNotificationEvent = new SendMobilePushNotificationIntegrationEvent(
                FCMTokens,  "Gói đăng ký đã được kích hoạt",
                "Gói đăng ký của bạn đã được kích hoạt thành công.");

            await publishEndpoint.Publish(sendMobilePushNotificationEvent, cancellationToken);
        }

        await publishEndpoint.Publish(activateSubscriptionEvent, cancellationToken);
        await publishEndpoint.Publish(sendEmailEvent, cancellationToken);
    }
}