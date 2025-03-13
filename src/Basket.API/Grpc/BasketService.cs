using System.Diagnostics.CodeAnalysis;
using eShop.Basket.API.Repositories;
using eShop.Basket.API.Extensions;
using eShop.Basket.API.Model;
using eShop.WebApp;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace eShop.Basket.API.Grpc;

public class BasketService(
    IBasketRepository repository,
    ILogger<BasketService> logger,
    Instrumentation instrumentation
    ) : Basket.BasketBase

{

    private ActivitySource activitySource = instrumentation.ActivitySource;

    private string MaskId(string id)
    {
        return string.IsNullOrEmpty(id) ? id : $"{id[..^20]}********************";
    }

    [AllowAnonymous]
    public override async Task<CustomerBasketResponse> GetBasket(GetBasketRequest request, ServerCallContext context)
    {

        using (var myActivity = activitySource.StartActivity("[BasketAPI] GetBasket"))
        {
            var userId = context.GetUserIdentity();
           
            if (string.IsNullOrEmpty(userId))
            {
                return new();
            }
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Begin GetBasketById call from method {Method} for basket id {Id}", context.Method, MaskId(userId));
            }
            myActivity?.SetTag("[BasketAPI] userId", MaskId(userId));
            var data = await repository.GetBasketAsync(userId);
            if (data is not null)
            {
                myActivity?.SetTag("[BasketAPI] basketItems", data.Items.Count);
                return MapToCustomerBasketResponse(data);
            }
            return new();
        }
    }

    public override async Task<CustomerBasketResponse> UpdateBasket(UpdateBasketRequest request, ServerCallContext context)
    {
        using (var myActivity = activitySource.StartActivity("[BasketAPI] UpdateBasket"))
        {
            var userId = context.GetUserIdentity();
            if (string.IsNullOrEmpty(userId))
            {
                ThrowNotAuthenticated();
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Begin UpdateBasket call from method {Method} for basket id {Id}", context.Method, MaskId(userId));
            }
            myActivity?.SetTag("[BasketAPI]  userId", MaskId(userId));

            var customerBasket = MapToCustomerBasket(userId, request);
            var response = await repository.UpdateBasketAsync(customerBasket);
            if (response is null)
            {
                myActivity?.SetTag("[BasketAPI] updated", response);
                ThrowBasketDoesNotExist(userId);
            }

            return MapToCustomerBasketResponse(response);
        }
    }

    public override async Task<DeleteBasketResponse> DeleteBasket(DeleteBasketRequest request, ServerCallContext context)
    {
        var userId = context.GetUserIdentity();
        if (string.IsNullOrEmpty(userId))
        {
            ThrowNotAuthenticated();
        }

        await repository.DeleteBasketAsync(userId);
        return new();
    }

    [DoesNotReturn]
    private static void ThrowNotAuthenticated() => throw new RpcException(new Status(StatusCode.Unauthenticated, "The caller is not authenticated."));

    [DoesNotReturn]
    private static void ThrowBasketDoesNotExist(string userId) => throw new RpcException(new Status(StatusCode.NotFound, $"Basket with buyer id {userId} does not exist"));

    private static CustomerBasketResponse MapToCustomerBasketResponse(CustomerBasket customerBasket)
    {
        var response = new CustomerBasketResponse();

        foreach (var item in customerBasket.Items)
        {
            response.Items.Add(new BasketItem()
            {
                ProductId = item.ProductId,
                Quantity = item.Quantity,
            });
        }

        return response;
    }

    private static CustomerBasket MapToCustomerBasket(string userId, UpdateBasketRequest customerBasketRequest)
    {
        var response = new CustomerBasket
        {
            BuyerId = userId
        };

        foreach (var item in customerBasketRequest.Items)
        {
            response.Items.Add(new()
            {
                ProductId = item.ProductId,
                Quantity = item.Quantity,
            });
        }

        return response;
    }
}
