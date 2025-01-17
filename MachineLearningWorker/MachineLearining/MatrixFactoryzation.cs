﻿using EComerence.Core.Domain;
using EComerence.Core.Repositories;
using EComerence.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MachineLearningWorker.MachineLearining
{
   public class MatrixFactoryzation
   {
      private readonly IOrderListService _orderListService;
      private readonly IProductService _productService;
      private readonly IProductRepository _productRepository;

      private Dictionary<uint, Guid> guidToIntDictionary;

      public MatrixFactoryzation(IOrderListService orderListService, IProductService productService, IProductRepository productRepository)
      {
         _orderListService = orderListService;
         _productService = productService;
         _productRepository = productRepository;
      }



      public void GuidConverter(List<Guid> guids)
      {
         guidToIntDictionary = new Dictionary<uint, Guid>();
         uint i = 0;
         foreach (Guid guid in guids)
         {

            guidToIntDictionary.Add(i, guid);
            i++;
         }
      }

      public uint ConvertGuidToInt(Guid guid)
      {
         return guidToIntDictionary.ContainsValue(guid) ?
             guidToIntDictionary.Single(e => e.Value == guid).Key
             : throw new ArgumentException("GUID not found in the dictionary.");
      }

      public Guid ConvertIntToGuid(uint intValue)
      {
         if (guidToIntDictionary.TryGetValue(intValue, out Guid guid))
            return guid;

         throw new ArgumentException("Integer value not found in the dictionary.");
      }

      public Task PrepareData(out List<Guid> productList,out List<ProductEntry> productEntries )
      {
         var orderList = _orderListService.GetOrders().Result;
         productList = _productService.BrowseAsync().Result.Select(p => p.Id).ToList();
         GuidConverter(productList);

         productEntries = orderList
             .Select(pair => new ProductEntry
             {
                ProductID = ConvertGuidToInt(pair.Item1),
                CoPurchaseProductID = ConvertGuidToInt(pair.Item2)
             })
             .ToList();
         return Task.CompletedTask;
      }
      public async Task TrainAsync()
      {
         PrepareData(out List<Guid> productList, out List<ProductEntry> productEntries).Wait();

         //STEP 1: Create MLContext to be shared across the model creation workflow objects 
         MLContext mlContext = new MLContext();

         //STEP 2: Read the trained data using TextLoader by defining the schema for reading the product co-purchase dataset
         var trainData = mlContext.Data.LoadFromEnumerable<ProductEntry>(productEntries);


         //STEP 3: Your data is already encoded so all you need to do is specify options for MatrxiFactorizationTrainer with a few extra hyperparameters
         MatrixFactorizationTrainer.Options options = new MatrixFactorizationTrainer.Options
         {

            MatrixColumnIndexColumnName = nameof(ProductEntry.ProductID),
            MatrixRowIndexColumnName = nameof(ProductEntry.CoPurchaseProductID),
            LabelColumnName = "Score",
            LossFunction = MatrixFactorizationTrainer.LossFunctionType.SquareLossOneClass,
            NumberOfIterations = 10,
            NumberOfThreads = 1,
            ApproximationRank = 2,
         };

         //Step 4: Call the MatrixFactorization trainer by passing options.
         var est = mlContext.Recommendation().Trainers.MatrixFactorization(options);


         //STEP 5: Train the model fitting to the DataSet
         ITransformer model = est.Fit(trainData);

         //STEP 6: Create prediction engine
         //        The higher the score the higher the probability for this particular productID being co-purchased 
         var predictionEngine = mlContext.Model.CreatePredictionEngine<ProductEntry, Copurchase_prediction>(model);


         //STEP 7: Create product recommendations for the specified product
         List<(Guid, Guid, float)> recommendations = new();

         foreach (var productId in productList)
         {
            Guid maxCoPurchaseProductId = Guid.Empty;

            foreach (var coPurchaseProductId in productList)
            {
               if (coPurchaseProductId != productId)
               {
                  var prediction = predictionEngine.Predict(new ProductEntry
                  {
                     ProductID = ConvertGuidToInt(productId),
                     CoPurchaseProductID = ConvertGuidToInt(coPurchaseProductId)
                  });

                  recommendations.Add((productId, coPurchaseProductId, prediction.Score));
               }
            }

         }
         await SaveChanges(recommendations);
         Console.WriteLine("=============== End of process, hit any key to finish ===============");
         Console.ReadKey();
      }


      private async Task SaveChanges(List<(Guid, Guid, float)> recommendations)
      {
         var products = await _productRepository.BrowseAsync();
         foreach (var product in products)
         {
            var guid = recommendations.Where(x => x.Item1 == product.Id)
           .OrderByDescending(x => x.Item3)
           .Select(x => x.Item2)
           .FirstOrDefault();

            var recomendations = recommendations.Where(x => x.Item1 == product.Id)
                .OrderByDescending(x => x.Item3)
                .Select( x=> new KeyValuePair<Guid,float> (x.Item2, x.Item3 ))
                .Take(30)
                .ToDictionary(x => x.Key, x => x.Value);

            product.SetCopurchasedProductId(guid);
            product.SetRecomendations(recomendations);
         }
         await _productRepository.UpdateBulkAsync(products);
      }

   }

   public class Copurchase_prediction
   {
      public float Score { get; set; }
   }

   public class ProductEntry
   {
      [LoadColumn(0)]
      [KeyType(count: 262111)] // this transforms string to key
      public uint ProductID { get; set; }

      [LoadColumn(1)]
      [KeyType(count: 262111)]
      public uint CoPurchaseProductID { get; set; }

      public float Score { get; set; }
   }
}
