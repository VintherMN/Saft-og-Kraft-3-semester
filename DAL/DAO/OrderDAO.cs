﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DAL.Model;
using Dapper;

namespace DAL.DAO;
public class OrderDAO : BaseDAO, IOrderDAO
{
    // Queries defined as readonly fields
    private static readonly string InsertOrderSql = @"
        INSERT INTO [Order] (OrderDate, CustomerId, TotalAmount)
        OUTPUT INSERTED.OrderId
        VALUES (@OrderDate, @CustomerId, @TotalAmount);";

    private static readonly string InsertOrderLineSql = @"
        INSERT INTO OrderLine (OrderId, ProductId, Quantity, UnitPrice)
        VALUES (@OrderId, @ProductId, @Quantity, @UnitPrice);";

    private static readonly string UpdateProductStockSql = @"
        UPDATE Product
        SET Stock = Stock - @Quantity
        WHERE ProductId = @ProductId AND Stock >= @Quantity";

    private static readonly string GetProductStockAndVersionSql = @"
        SELECT Stock, Version FROM Product WHERE ProductId = @ProductId;";

    private static readonly string GetAllOrders = @"
        SELECT * FROM [Order]";

    private static readonly string GetOrderLines = @"
        SELECT ol.OrderLineId, ol.OrderId, ol.Quantity, ol.UnitPrice, 
          p.ProductId, p.ProductName FROM OrderLine ol INNER JOIN Product p 
          ON ol.ProductId = p.ProductId INNER JOIN [Order] o ON ol.OrderId = o.OrderId 
          WHERE ol.OrderId = @OrderId;";

    private static readonly string UpdateOrderStatus = @"
         UPDATE [Order] SET Status = @Status WHERE OrderId = @OrderId;";




    // Constructor for OrderDAO that takes a Connection String.    
    public OrderDAO(string connectionString) : base(connectionString)
    {
    }

    #region !!-- Crud section for Order. --!!

    // This async method CreateOrder is used for creating an order for the sale on the website.
    // We do so by first checking if we have the requested products in stock. 
    // Then If we have the products we insert an order in DB.
    // Thereafter we insert OrderLines into DB
    // if any of these fails we rollback
    public async Task<Order> CreateOrderAsync(Order entity)
    {
        using var connection = CreateConnection();
        connection.Open();


        // Step 1: Validate Stock for All Products
        var insufficientStockProducts = new Dictionary<int, byte[]>(); // ProductId -> Version
        foreach (var orderLine in entity.OrderLines)
        {
            var productData = await connection.QuerySingleOrDefaultAsync<(int Stock, byte[] Version)>(
                GetProductStockAndVersionSql,
                new { ProductId = orderLine.ProductId });

            Console.WriteLine($"ProductId: {orderLine.ProductId}, Stock: {productData.Stock}, Version: {Convert.ToBase64String(productData.Version)}");

            if (productData == default || productData.Stock < orderLine.Quantity)
            {
                Console.WriteLine($"Insufficient stock for ProductId {orderLine.ProductId}.");
                throw new InvalidOperationException($"Insufficient stock for ProductId {orderLine.ProductId}.");
            }

            insufficientStockProducts[orderLine.ProductId] = productData.Version;
        }
        Thread.Sleep( 5000 );
        // Step 2: Transaction for Order Creation
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadUncommitted); 
        try
        {
            // Insert Order
            var orderId = await connection.QuerySingleAsync<int>(
                InsertOrderSql,
                entity,
                transaction);

            entity.OrderId = orderId;

            // Insert OrderLines and Update Stock
            foreach (var orderLine in entity.OrderLines)
            {
                // Insert OrderLine
                await connection.ExecuteAsync(
                    InsertOrderLineSql,
                    new
                    {
                        OrderId = entity.OrderId,
                        ProductId = orderLine.ProductId,
                        Quantity = orderLine.Quantity,
                        UnitPrice = orderLine.UnitPrice
                    },
                    transaction);

                var rowsAffected = await connection.ExecuteAsync(
                    UpdateProductStockSql,
                    new
                    {
                        ProductId = orderLine.ProductId,
                        Quantity = orderLine.Quantity,
                        
                    },
                    transaction);

                if (rowsAffected == 0)
                {
                    throw new InvalidOperationException($"Failed to update stock for ProductId {orderLine.ProductId}. Stock may have been modified by another transaction.");
                }
            }

            // Commit Transaction
            transaction.Commit();
            return entity;
        }

        catch
        {
            // Rollback Transaction in Case of Error
            transaction.Rollback();
            throw new DataException("Error creating order and updating stock");
        }
    }



    // This method retrieves all orders from the database
    public async Task<IEnumerable<Order>> GetAllOrdersAsync()
    {
        try
        {
            using var connection = CreateConnection();
            return (await connection.QueryAsync<Order>(GetAllOrders)).ToList();
        }
        catch (Exception ex)
        {
            throw new Exception($"Error fetching all orders: {ex.Message}", ex);
        }
    }

    // This method retrieves all order lines associated with a specific order ID from the database
    public async Task<IEnumerable<OrderLine>> GetOrderLinesAsync(int orderId)
    {
        try
        {
            using var connection = CreateConnection();
            return (await connection.QueryAsync<OrderLine>(GetOrderLines, new { OrderId = orderId })).ToList();
        }
        catch (Exception ex)
        {
            throw new Exception($"Error fetching order lines for order ID {orderId}: {ex.Message}", ex);
        }
    }

    public async Task<bool> UpdateOrderStatusAsync(int orderId, string status)
    {
        try
        {
            using var connection = CreateConnection();
            var result = await connection.ExecuteAsync(UpdateOrderStatus, new { Status = status, OrderId = orderId });
            return result > 0;

        }
        catch (Exception ex)
        {
            throw new Exception($"Error updating order status: {ex.Message}", ex);
        }
    }
    #endregion

}

