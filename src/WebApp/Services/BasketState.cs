using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using eShop.WebAppComponents.Catalog;
using eShop.WebAppComponents.Services;
using System.Diagnostics;
using eShop.WebApp;
using System.Diagnostics.Metrics;

using NLog;



public class BasketState(
    BasketService basketService,
    CatalogService catalogService,
    OrderingService orderingService,
    AuthenticationStateProvider authenticationStateProvider,
    Instrumentation instrumentation
    ) : IBasketState
{
    
    private Task<IReadOnlyCollection<BasketItem>>? _cachedBasket;
    private HashSet<BasketStateChangedSubscription> _changeSubscriptions = new();
    
    private ActivitySource activitySource = instrumentation.ActivitySource;

    private static readonly Meter basketMeter = new Meter("eShop.WebApp.Basket");
    private static readonly Counter<int> _basketAddCounter = basketMeter.CreateCounter<int>(
     name: "basket_items_total",  
     unit: "{items}",            
     description: "Total number of items added to basket"
 );

    private static readonly Histogram<double> _basketOperationDuration = basketMeter.CreateHistogram<double>(
        name: "basket_operation_duration_seconds", 
        unit: "s",                                
        description: "Duration of basket operations"
    );

    public Task DeleteBasketAsync()
        => basketService.DeleteBasketAsync();

    public async Task<IReadOnlyCollection<BasketItem>> GetBasketItemsAsync()
        => (await GetUserAsync()).Identity?.IsAuthenticated == true
        ? await FetchBasketItemsAsync()
        : [];

    public IDisposable NotifyOnChange(EventCallback callback)
    {
        var subscription = new BasketStateChangedSubscription(this, callback);
        _changeSubscriptions.Add(subscription);
        return subscription;
    }
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private string MaskUser(string user)
    {

        return (string.IsNullOrEmpty(user) || user.Length <= 2) ? user: $"{user[0]}***{user[^1]}";
    }

    private string MaskId(string id)
    {
        return string.IsNullOrEmpty(id) ? id : $"{id[..^20]}********************";
    }


    public async Task AddAsync(CatalogItem item)
    {
        var user = await authenticationStateProvider.GetUserNameAsync();
        var userId = await authenticationStateProvider.GetBuyerIdAsync();

        var start_time = DateTime.UtcNow;
        if (user == null || userId == null)
        {
            throw new InvalidOperationException("User is not logged");
        }
        Logger.Info("User {User} with ID {UserId} is adding item to basket: {ProductId}", MaskUser(user), MaskId(userId), item.Id);

        _basketAddCounter.Add(1, new KeyValuePair<string, object?>("event", "start"));
        Logger.Info("Adding item to basket: {ProductId}", item.Id);

        using (var myActivity = activitySource.StartActivity("Add_item"))
        {
            try
            {
                myActivity?.SetTag("product.id", item.Id);

                myActivity?.SetTag("operation.fetch.start", "Starting to fetch basket items");
                var items = (await FetchBasketItemsAsync()).Select(i => new BasketQuantity(i.ProductId, i.Quantity)).ToList();
                myActivity?.SetTag("operation.fetch.complete", "Basket items retrieved");
                myActivity?.SetTag("basket.items.count", items.Count);

                bool found = false;
                for (var i = 0; i < items.Count; i++)
                {
                    var existing = items[i];
                    if (existing.ProductId == item.Id)
                    {
                        items[i] = existing with { Quantity = existing.Quantity + 1 };
                        found = true;
                        myActivity?.SetTag("operation.type", "update_quantity");
                        myActivity?.SetTag("item.quantity.new", existing.Quantity + 1);
                        break;
                    }
                }

                if (!found)
                {
                    _basketAddCounter.Add(1, new KeyValuePair<string, object?>("event", "new_item"));
                    items.Add(new BasketQuantity(item.Id, 1));
                    myActivity?.SetTag("operation.type", "add_new_item");
                }

                myActivity?.SetTag("operation.update.start", "Starting basket update");
                _cachedBasket = null;
                await basketService.UpdateBasketAsync(items);
                myActivity?.SetTag("operation.update.complete", "Basket updated");

                myActivity?.SetTag("operation.notify.start", "Starting notifications");
                await NotifyChangeSubscribersAsync();
                myActivity?.SetTag("operation.notify.complete", "Notifications sent");

                _basketAddCounter.Add(1, new KeyValuePair<string, object?>[] {
                    new("operation", "add"),
                    new("product_id", item.Id.ToString())
                });

                _basketOperationDuration.Record(
                    (DateTime.UtcNow - start_time).TotalSeconds,
                    new KeyValuePair<string, object?>[] {
                    new("operation", "add_item")
                    }
                );

                myActivity?.SetTag("operation.status", "success");
            }
            catch (Exception exe)
            {
                _basketAddCounter.Add(1, new KeyValuePair<string, object?>[] {
                new("operation_status", "error"),
                new("error_type", exe.GetType().Name)
            });
                myActivity?.SetTag("operation.status", "error");
                myActivity?.SetTag("error.message", exe.Message);
                throw;
            }

        }
    }

    public async Task SetQuantityAsync(int productId, int quantity)
    {
        var existingItems = (await FetchBasketItemsAsync()).ToList();
        if (existingItems.FirstOrDefault(row => row.ProductId == productId) is { } row)
        {
            if (quantity > 0)
            {
                row.Quantity = quantity;
            }
            else
            {
                existingItems.Remove(row);
            }

            _cachedBasket = null;
            await basketService.UpdateBasketAsync(existingItems.Select(i => new BasketQuantity(i.ProductId, i.Quantity)).ToList());
            await NotifyChangeSubscribersAsync();
        }
    }

    public async Task CheckoutAsync(BasketCheckoutInfo checkoutInfo)
    {
        if (checkoutInfo.RequestId == default)
        {
            checkoutInfo.RequestId = Guid.NewGuid();
        }

        var buyerId = await authenticationStateProvider.GetBuyerIdAsync() ?? throw new InvalidOperationException("User does not have a buyer ID");
        var userName = await authenticationStateProvider.GetUserNameAsync() ?? throw new InvalidOperationException("User does not have a user name");

        // Get details for the items in the basket
        var orderItems = await FetchBasketItemsAsync();

        // Call into Ordering.API to create the order using those details
        var request = new CreateOrderRequest(
            UserId: buyerId,
            UserName: userName,
            City: checkoutInfo.City!,
            Street: checkoutInfo.Street!,
            State: checkoutInfo.State!,
            Country: checkoutInfo.Country!,
            ZipCode: checkoutInfo.ZipCode!,
            CardNumber: "1111222233334444",
            CardHolderName: "TESTUSER",
            CardExpiration: DateTime.UtcNow.AddYears(1),
            CardSecurityNumber: "111",
            CardTypeId: checkoutInfo.CardTypeId,
            Buyer: buyerId,
            Items: [.. orderItems]);
        await orderingService.CreateOrder(request, checkoutInfo.RequestId);
        await DeleteBasketAsync();
    }

    private Task NotifyChangeSubscribersAsync()
        => Task.WhenAll(_changeSubscriptions.Select(s => s.NotifyAsync()));

    private async Task<ClaimsPrincipal> GetUserAsync()
        => (await authenticationStateProvider.GetAuthenticationStateAsync()).User;

    private Task<IReadOnlyCollection<BasketItem>> FetchBasketItemsAsync()
    {
        return _cachedBasket ??= FetchCoreAsync();

        async Task<IReadOnlyCollection<BasketItem>> FetchCoreAsync()
        {
            var quantities = await basketService.GetBasketAsync();
            if (quantities.Count == 0)
            {
                return [];
            }

            // Get details for the items in the basket
            var basketItems = new List<BasketItem>();
            var productIds = quantities.Select(row => row.ProductId);
            var catalogItems = (await catalogService.GetCatalogItems(productIds)).ToDictionary(k => k.Id, v => v);
            foreach (var item in quantities)
            {
                var catalogItem = catalogItems[item.ProductId];
                var orderItem = new BasketItem
                {
                    Id = Guid.NewGuid().ToString(), // TODO: this value is meaningless, use ProductId instead.
                    ProductId = catalogItem.Id,
                    ProductName = catalogItem.Name,
                    UnitPrice = catalogItem.Price,
                    Quantity = item.Quantity,
                };
                basketItems.Add(orderItem);
            }

            return basketItems;
        }
    }

    private class BasketStateChangedSubscription(BasketState Owner, EventCallback Callback) : IDisposable
    {
        public Task NotifyAsync() => Callback.InvokeAsync();
        public void Dispose() => Owner._changeSubscriptions.Remove(this);
    }
}

public record CreateOrderRequest(
    string UserId,
    string UserName,
    string City,
    string Street,
    string State,
    string Country,
    string ZipCode,
    string CardNumber,
    string CardHolderName,
    DateTime CardExpiration,
    string CardSecurityNumber,
    int CardTypeId,
    string Buyer,
    List<BasketItem> Items);
