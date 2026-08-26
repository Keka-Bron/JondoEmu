using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    public readonly record struct RecipeIngredient(int ItemId, int Quantity);

    public sealed class RecipeDefinition
    {
        private readonly List<RecipeIngredient> _ingredients = new List<RecipeIngredient>();
        public int ResultId { get; init; }
        public long ResultNameId { get; init; }
        public int ResultTypeId { get; init; }
        public int ResultLevel { get; init; }
        public int JobId { get; init; }
        public int SkillId { get; init; }
        public IReadOnlyList<RecipeIngredient> Ingredients => _ingredients;
        internal List<RecipeIngredient> MutableIngredients => _ingredients;
    }

    /// <summary>Recipe catalogue with indexes used by craft and inventory handlers.</summary>
    public static class RecipeManager
    {
        private static IReadOnlyDictionary<int, RecipeDefinition> _byResult =
            new Dictionary<int, RecipeDefinition>();
        private static IReadOnlyDictionary<int, IReadOnlyList<RecipeDefinition>> _bySkill =
            new Dictionary<int, IReadOnlyList<RecipeDefinition>>();

        public static int Count => _byResult.Count;
        public static IEnumerable<RecipeDefinition> All => _byResult.Values;
        public static bool TryGetByResult(int resultId, out RecipeDefinition recipe)
            => _byResult.TryGetValue(resultId, out recipe!);
        public static IReadOnlyList<RecipeDefinition> ForSkill(int skillId)
            => _bySkill.TryGetValue(skillId, out var recipes) ? recipes : Array.Empty<RecipeDefinition>();

        public static void Initialize()
        {
            ImportIfAvailable();
            LoadFromDatabase();
            Console.WriteLine($"[Recipes] {_byResult.Count} recetas cargadas.");
        }

        private static void ImportIfAvailable()
        {
            string path = Paths.RecipesJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Recipes] Falta {path}; se usa el catalogo que ya hay en la base.");
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var rows = new List<RecipeDefinition>();
                foreach (var data in DofusDudeCatalog.Rows(document))
                {
                    var ingredientIds = DofusDudeCatalog.IntArray(data, "ingredientIds");
                    var quantities = DofusDudeCatalog.IntArray(data, "quantities");
                    if (ingredientIds.Count != quantities.Count)
                        throw new InvalidOperationException(
                            $"Recette {DofusDudeCatalog.Int32(data, "resultId")}: ingredientes y cantidades descuadrados.");

                    var recipe = new RecipeDefinition
                    {
                        ResultId = DofusDudeCatalog.Int32(data, "resultId"),
                        ResultNameId = DofusDudeCatalog.Int64(data, "resultNameId"),
                        ResultTypeId = DofusDudeCatalog.Int32(data, "resultTypeId"),
                        ResultLevel = DofusDudeCatalog.Int32(data, "resultLevel"),
                        JobId = DofusDudeCatalog.Int32(data, "jobId"),
                        SkillId = DofusDudeCatalog.Int32(data, "skillId"),
                    };
                    for (int i = 0; i < ingredientIds.Count; i++)
                        recipe.MutableIngredients.Add(new RecipeIngredient(ingredientIds[i], quantities[i]));
                    rows.Add(recipe);
                }
                if (rows.Count == 0) throw new InvalidOperationException("El catalogo de recetas esta vacio.");
                Import(rows);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Recipes] Importacion cancelada, se conserva el catalogo de la base: {ex.Message}");
            }
        }

        private static void Import(List<RecipeDefinition> rows)
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            foreach (string table in new[] { "RecipeIngredients", "Recipes" })
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table};";
                delete.ExecuteNonQuery();
            }

            using var recipeCommand = connection.CreateCommand();
            recipeCommand.Transaction = transaction;
            recipeCommand.CommandText = @"INSERT INTO Recipes
                (ResultId,ResultNameId,ResultTypeId,ResultLevel,JobId,SkillId)
                VALUES($result,$name,$type,$level,$job,$skill);";
            foreach (string parameter in new[] { "$result", "$name", "$type", "$level", "$job", "$skill" })
                recipeCommand.Parameters.Add(parameter, SqliteType.Integer);

            using var ingredientCommand = connection.CreateCommand();
            ingredientCommand.Transaction = transaction;
            ingredientCommand.CommandText = @"INSERT INTO RecipeIngredients
                (ResultId,Position,IngredientId,Quantity) VALUES($result,$position,$item,$quantity);";
            ingredientCommand.Parameters.Add("$result", SqliteType.Integer);
            ingredientCommand.Parameters.Add("$position", SqliteType.Integer);
            ingredientCommand.Parameters.Add("$item", SqliteType.Integer);
            ingredientCommand.Parameters.Add("$quantity", SqliteType.Integer);

            foreach (var row in rows)
            {
                object[] values = { row.ResultId, row.ResultNameId, row.ResultTypeId,
                                    row.ResultLevel, row.JobId, row.SkillId };
                for (int i = 0; i < recipeCommand.Parameters.Count; i++)
                    recipeCommand.Parameters[i].Value = values[i];
                recipeCommand.ExecuteNonQuery();

                for (int i = 0; i < row.Ingredients.Count; i++)
                {
                    ingredientCommand.Parameters["$result"].Value = row.ResultId;
                    ingredientCommand.Parameters["$position"].Value = i;
                    ingredientCommand.Parameters["$item"].Value = row.Ingredients[i].ItemId;
                    ingredientCommand.Parameters["$quantity"].Value = row.Ingredients[i].Quantity;
                    ingredientCommand.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }

        private static void LoadFromDatabase()
        {
            var recipes = new Dictionary<int, RecipeDefinition>();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT ResultId,ResultNameId,ResultTypeId,ResultLevel,JobId,SkillId
                                        FROM Recipes ORDER BY ResultId;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var recipe = new RecipeDefinition
                    {
                        ResultId=reader.GetInt32(0), ResultNameId=reader.GetInt64(1),
                        ResultTypeId=reader.GetInt32(2), ResultLevel=reader.GetInt32(3),
                        JobId=reader.GetInt32(4), SkillId=reader.GetInt32(5),
                    };
                    recipes.Add(recipe.ResultId, recipe);
                }
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT ResultId,IngredientId,Quantity FROM RecipeIngredients
                                        ORDER BY ResultId,Position;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (recipes.TryGetValue(reader.GetInt32(0), out var recipe))
                        recipe.MutableIngredients.Add(new RecipeIngredient(reader.GetInt32(1), reader.GetInt32(2)));
                }
            }

            var building = new Dictionary<int, List<RecipeDefinition>>();
            foreach (var recipe in recipes.Values)
            {
                if (!building.TryGetValue(recipe.SkillId, out var list))
                    building[recipe.SkillId] = list = new List<RecipeDefinition>();
                list.Add(recipe);
            }
            var bySkill = new Dictionary<int, IReadOnlyList<RecipeDefinition>>();
            foreach (var pair in building) bySkill[pair.Key] = pair.Value;
            _byResult = recipes;
            _bySkill = bySkill;
        }
    }
}
