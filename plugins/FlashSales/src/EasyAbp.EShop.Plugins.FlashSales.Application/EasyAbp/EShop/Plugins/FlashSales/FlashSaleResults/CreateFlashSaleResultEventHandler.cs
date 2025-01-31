﻿using System.Threading.Tasks;
using EasyAbp.EShop.Plugins.FlashSales.FlashSalePlans;
using EasyAbp.EShop.Plugins.FlashSales.FlashSaleResults.Dtos;
using EasyAbp.Eshop.Products.Products;
using EasyAbp.EShop.Products.Products;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.DistributedLocking;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.MultiTenancy;
using Volo.Abp.ObjectExtending;
using Volo.Abp.ObjectMapping;
using Volo.Abp.Uow;

namespace EasyAbp.EShop.Plugins.FlashSales.FlashSaleResults;

public class CreateFlashSaleResultEventHandler : IDistributedEventHandler<CreateFlashSaleResultEto>,
    ITransientDependency
{
    protected IObjectMapper ObjectMapper { get; }
    protected ICurrentTenant CurrentTenant { get; }
    protected IUnitOfWorkManager UnitOfWorkManager { get; }
    protected ILogger<CreateFlashSaleResultEventHandler> Logger { get; }
    protected IAbpDistributedLock AbpDistributedLock { get; }
    protected IDistributedEventBus DistributedEventBus { get; }
    protected IAbpApplication AbpApplication { get; }
    protected IFlashSaleInventoryManager FlashSaleInventoryManager { get; }
    protected IFlashSaleCurrentResultCache FlashSaleCurrentResultCache { get; }
    protected IFlashSaleResultRepository FlashSaleResultRepository { get; }

    public CreateFlashSaleResultEventHandler(
        IObjectMapper objectMapper,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        ILogger<CreateFlashSaleResultEventHandler> logger,
        IAbpDistributedLock abpDistributedLock,
        IDistributedEventBus distributedEventBus,
        IAbpApplication abpApplication,
        IFlashSaleInventoryManager flashSaleInventoryManager,
        IFlashSaleCurrentResultCache flashSaleCurrentResultCache,
        IFlashSaleResultRepository flashSaleResultRepository)
    {
        ObjectMapper = objectMapper;
        CurrentTenant = currentTenant;
        UnitOfWorkManager = unitOfWorkManager;
        Logger = logger;
        AbpDistributedLock = abpDistributedLock;
        DistributedEventBus = distributedEventBus;
        AbpApplication = abpApplication;
        FlashSaleInventoryManager = flashSaleInventoryManager;
        FlashSaleCurrentResultCache = flashSaleCurrentResultCache;
        FlashSaleResultRepository = flashSaleResultRepository;
    }

    [UnitOfWork(true)]
    public virtual async Task HandleEventAsync(CreateFlashSaleResultEto eventData)
    {
        await using var handle = await AbpDistributedLock.TryAcquireAsync(await GetLockKeyAsync(eventData));

        if (handle is null)
        {
            throw new AbpException("Concurrent flash sale result creation");
        }

        var ongoingResult = await FlashSaleResultRepository.FirstOrDefaultAsync(x =>
            x.PlanId == eventData.Plan.Id &&
            x.UserId == eventData.UserId &&
            x.Status != FlashSaleResultStatus.Failed);

        if (ongoingResult is not null)
        {
            Logger.LogWarning("Duplicate ongoing flash sale result creation");

            await FlashSaleCurrentResultCache.SetAsync(eventData.Plan.Id, eventData.UserId,
                new FlashSaleCurrentResultCacheItem
                {
                    TenantId = ongoingResult.TenantId,
                    ResultDto = ObjectMapper.Map<FlashSaleResult, FlashSaleResultDto>(ongoingResult)
                });

            // try to roll back the inventory.
            UnitOfWorkManager.Current.OnCompleted(async () =>
            {
                using var scope = AbpApplication.ServiceProvider.CreateScope();

                var flashSaleInventoryManager = scope.ServiceProvider.GetRequiredService<IFlashSaleInventoryManager>();

                if (!await flashSaleInventoryManager.TryRollBackInventoryAsync(eventData.TenantId,
                        eventData.ProductInventoryProviderName, eventData.Plan.StoreId,
                        eventData.Plan.ProductId, eventData.Plan.ProductSkuId))
                {
                    Logger.LogWarning("Failed to roll back the flash sale inventory.");
                }
            });

            return; // avoid to create a result entity and an order.
        }

        var result = new FlashSaleResult(
            id: eventData.ResultId,
            tenantId: CurrentTenant.Id,
            storeId: eventData.Plan.StoreId,
            planId: eventData.Plan.Id,
            userId: eventData.UserId,
            reducedInventoryTime: eventData.ReducedInventoryTime
        );

        var eto = new CreateFlashSaleOrderEto
        {
            TenantId = eventData.TenantId,
            ResultId = eventData.ResultId,
            UserId = eventData.UserId,
            CustomerRemark = eventData.CustomerRemark,
            Plan = eventData.Plan,
            HashToken = eventData.HashToken
        };

        eventData.MapExtraPropertiesTo(eto, MappingPropertyDefinitionChecks.None);

        await DistributedEventBus.PublishAsync(eto);

        await FlashSaleResultRepository.InsertAsync(result, autoSave: true);
    }

    protected virtual Task<string> GetLockKeyAsync(CreateFlashSaleResultEto eventData)
    {
        return Task.FromResult($"eshopflashsales-creating-result_{eventData.Plan.Id}-{eventData.UserId}");
    }
}