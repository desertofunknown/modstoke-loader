using System.Reflection;
using System.Text.RegularExpressions;
using Construction;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using RedLoader;
using RedLoader.Utils;
using Sons.Crafting;
using Sons.Crafting.Structures;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SonsSdk.Building;

internal interface ISdkBlueprintRegistrationDriver
{
    void Register(ScrewStructureRegistration registration);
    Task LoadGltfObjectsFromDirectory(string directory);
    int? GetRecipeIdFromName(string name);
    void OnGameActivated();
    void SetupRecipeIngredients(StructureRecipe recipe);
    void PrepareBuiltStructure(GameObject built, StructureRecipe recipe);
    bool AddRecipeToDatabase(StructureRecipe recipe);
    StructureRecipe CreateNewRecipe(int id, string name, StructureCraftingNode node, GameObject built);
    StructureCraftingNode CreateCraftingNode(Transform transformToWrap, int? ingredientItemId);
    void SetupObjectAsCraftingNode(ScrewStructureRegistration registration);
    void ProcessFreeformStructure(Transform freeformStructure);
    List<StructureCraftingNodeIngredient> ProcessStructure(Transform structureRoot, int itemId);
    Il2CppArrayBase<StructureCraftingNodeIngredient> GetIngredients(Transform structureRoot);
    void InitObjectInteraction(StructureCraftingNode node);
    void InitRecipeLocalization(StructureRecipe recipe);
    void CreateBookPage(StructureRecipe topRecipe, StructureRecipe bottomRecipe, Texture2D background);
    string ToDisplayName(string name);
}

internal interface ISdkBlueprintFallbackDriver
{
    void TryRegister(ScrewStructureRegistration reg);
    void OnGameActivated();
    Task Init();
    void OnSceneInit(ESonsScene scene);
    Task LoadGltfObjectsFromDirectory(string directory);
    void InitializePrefabs();
    void DiscardPrefabCandidate(Object candidate);
    void ReportPrefabCleanupFailure(Exception exception);
    void PrepareBuiltStructure(GameObject built, StructureRecipe recipe);
    void ProcessFreeformStructure(Transform freeformStructure);
    List<StructureCraftingNodeIngredient> ProcessStructure(Transform structureRoot, int itemId);
    Il2CppArrayBase<StructureCraftingNodeIngredient> GetIngredients(Transform structureRoot);
    StructureCraftingNode CreateCraftingNode(
        Transform transformToWrap,
        int? ingredientItemId);
    void InitObjectInteraction(StructureCraftingNode node);
    void SetupRecipeIngredients(StructureRecipe recipe);
    bool AddRecipeToDatabase(StructureRecipe recipe);
    StructureRecipe CreateNewRecipe(int id, string name, StructureCraftingNode node, GameObject built);
    void InitRecipeLocalization(StructureRecipe recipe);
    void InitRecipe(StructureRecipe recipe);
    void CreateBookPage(StructureRecipe topRecipe, StructureRecipe bottomRecipe, Texture2D background);
    void SetupObjectAsCraftingNode(ScrewStructureRegistration reg);
}

public record ScrewStructureRegistration(
    GameObject prefab,
    int recipeId,
    string recipeName);

public class CustomBlueprintManager
{
    private static readonly Lazy<ISdkBlueprintFallbackDriver> BlueprintFallback = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.BlueprintFallback", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkBlueprintFallbackDriver>)method.CreateDelegate(typeof(Func<ISdkBlueprintFallbackDriver>)))();
    });
    
    // For GLTF blueprints they first will be loaded from the Blueprints folder during OnSdkInitialized
    // and put into LoadedObjects. On game activation a StructureCraftingNode will be created and initialized from those.
    // Additionally StructureRecipes Will be created and registered. The created Crafting Nodes and Structure Recipes persist
    // between game sessions (as they are put into DontDestroyOnLoad) and thus only get created once.
    
    internal static bool ServerMode => LoaderEnvironment.IsDedicatedServer;

    public static MelonEvent<ScrewStructureRegistration> OnPrefabLoaded = new();
    public static MelonEvent<StructureCraftingNode> OnCraftingNodeCreated = new();
    
    public static string NextLocalizationString = "BLUEPRINT_PAGE_MISC";
    public static StructureRecipe.CategoryType StructureCategory = StructureRecipe.CategoryType.Decoration;
    
    private static readonly Regex PascalToHumanRegex = new("[a-z][A-Z]", RegexOptions.Compiled);
    internal static StructureCraftingNode _craftingNodePrefab;
    internal static StructureRecipe _recipePrefab;
    internal static readonly Dictionary<int, ScrewStructureRegistration> LoadedObjects = new();
    internal static readonly Dictionary<int, StructureRecipe> ProcessedObjects = new();

    /// <summary>
    /// Make an empty crafting node setup from the lean to structure.
    /// </summary>
    internal static void InitializePrefabs()
    {
        BlueprintFallback.Value.InitializePrefabs();
    }

    internal static void DiscardPrefabCandidate(Object candidate)
    {
        BlueprintFallback.Value.DiscardPrefabCandidate(candidate);
    }

    internal static void ReportPrefabCleanupFailure(Exception exception)
    {
        BlueprintFallback.Value.ReportPrefabCleanupFailure(exception);
    }

    /// <summary>
    /// Register a Gameobject as a prefab for a CraftingNode.
    /// The prefab needs to have <see cref="StructureCraftingNodeIngredient"/>s on all meshed that should be buildable.
    /// Those will dictate what ingredient you need to built that specific component.
    /// The crafting node will be created at game activation. Subscribe to <see cref="OnCraftingNodeCreated"/> in order to process it further.
    /// </summary>
    /// <param name="reg">A structure that holds the prefab, recipe id and recipe name</param>
    /// <exception cref="Exception"></exception>
    public static void TryRegister(ScrewStructureRegistration reg)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Blueprints.Register(reg);
            return;
        }

        BlueprintFallback.Value.TryRegister(reg);
    }

    public static IEnumerable<ScrewStructureRegistration> GetRegistrations() => LoadedObjects.Values;
    public static IEnumerable<StructureRecipe> GetProcessedRecipes() => ProcessedObjects.Values;
    
    internal static void OnGameActivated()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Blueprints.OnGameActivated();
            return;
        }

        BlueprintFallback.Value.OnGameActivated();
    }

    internal static Task Init()
    {
        return BlueprintFallback.Value.Init();
    }

    internal static void Uninit()
    {
        
    }

    internal static void OnSceneInit(ESonsScene scene)
    {
        BlueprintFallback.Value.OnSceneInit(scene);
    }

    public static void SetupRecipeIngredients(StructureRecipe recipe)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Blueprints.SetupRecipeIngredients(recipe);
            return;
        }

        BlueprintFallback.Value.SetupRecipeIngredients(recipe);
    }

    /// <summary>
    /// Prepare the built variant of a structure by removing all crafting node specific things like <see cref="StructureCraftingNodeIngredient"/>s.
    /// Basically a conversion from blueprint to built (finished) structure.
    /// </summary>
    /// <param name="built"></param>
    /// <param name="recipe"></param>
    public static void PrepareBuiltStructure(GameObject built, StructureRecipe recipe)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Blueprints.PrepareBuiltStructure(built, recipe);
            return;
        }

        BlueprintFallback.Value.PrepareBuiltStructure(built, recipe);
    }

    /// <summary>
    /// Tries to register a recipe with the recipe database.
    /// Checks if an instance of the <see cref="StructureCraftingSystem"/> exists and if the recipe isn't already registered.
    /// </summary>
    /// <param name="recipe"></param>
    /// <returns>True if the recipe got registered successfully</returns>
    public static bool AddRecipeToDatabase(StructureRecipe recipe)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Blueprints.AddRecipeToDatabase(recipe);

        return BlueprintFallback.Value.AddRecipeToDatabase(recipe);
    }

    /// <summary>
    /// Creates and registers a new recipe.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="name"></param>
    /// <param name="node"></param>
    /// <param name="built"></param>
    /// <returns></returns>
    public static StructureRecipe CreateNewRecipe(int id, string name, StructureCraftingNode node = null, GameObject built = null)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Blueprints.CreateNewRecipe(id, name, node, built);

        return BlueprintFallback.Value.CreateNewRecipe(id, name, node, built);
    }

    /// <summary>
    /// Turn a transform containing <see cref="StructureElement"/>s into a ghost and add ingredient descriptors.
    /// </summary>
    /// <param name="freeformStructure"></param>
    public static void ProcessFreeformStructure(Transform freeformStructure)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Blueprints.ProcessFreeformStructure(freeformStructure);
            return;
        }

        BlueprintFallback.Value.ProcessFreeformStructure(freeformStructure);
    }

    /// <summary>
    /// Turn a transform containing <see cref="MeshRenderer"/>s into a ghost and add ingredient descriptors.
    /// Each renderer will be treated as a separate ingredient.
    /// </summary>
    /// <param name="structureRoot"></param>
    /// <param name="itemId">The item id for the ingredients.</param>
    public static List<StructureCraftingNodeIngredient> ProcessStructure(Transform structureRoot, int itemId)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Blueprints.ProcessStructure(structureRoot, itemId);

        return BlueprintFallback.Value.ProcessStructure(structureRoot, itemId);
    }

    /// <summary>
    /// Get all <see cref="StructureCraftingNodeIngredient"/>s in a transform and its children.
    /// </summary>
    /// <param name="structureRoot"></param>
    public static Il2CppArrayBase<StructureCraftingNodeIngredient> GetIngredients(Transform structureRoot)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        return driver != null
            ? driver.Blueprints.GetIngredients(structureRoot)
            : BlueprintFallback.Value.GetIngredients(structureRoot);
    }

    /// <summary>
    /// Wraps <paramref name="transformToWrap"/> with a crafting node setting up all ingredients and ghosts for the structure.
    /// <paramref name="transformToWrap"/> needs to have <see cref="StructureElement"/>s as children.
    /// </summary>
    /// <param name="transformToWrap"></param>
    /// <param name="ingredientItemId"></param>
    /// <returns></returns>
    public static StructureCraftingNode CreateCraftingNode(
        Transform transformToWrap,
        int? ingredientItemId = null)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Blueprints.CreateCraftingNode(transformToWrap, ingredientItemId);

        return BlueprintFallback.Value.CreateCraftingNode(transformToWrap, ingredientItemId);
    }

    public static void InitObjectInteraction(StructureCraftingNode node)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Blueprints.InitObjectInteraction(node);
            return;
        }

        BlueprintFallback.Value.InitObjectInteraction(node);
    }
    
    public static void InitRecipeLocalization(StructureRecipe recipe)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Blueprints.InitRecipeLocalization(recipe);
            return;
        }

        BlueprintFallback.Value.InitRecipeLocalization(recipe);
    }

    /// <summary>
    /// Adds a recipe to the database and creates a node prefab if it doesn't exist.
    /// </summary>
    /// <param name="recipe"></param>
    private static void InitRecipe(StructureRecipe recipe)
    {
        BlueprintFallback.Value.InitRecipe(recipe);
    }

    /// <summary>
    /// Setup a book page from one or two recipes and the background.
    /// Will automatically setup localization for the recipes.
    /// </summary>
    /// <param name="topRecipe"></param>
    /// <param name="bottomRecipe"></param>
    /// <param name="background"></param>
    public static void CreateBookPage(StructureRecipe topRecipe, StructureRecipe bottomRecipe, Texture2D background)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Blueprints.CreateBookPage(topRecipe, bottomRecipe, background);
            return;
        }

        BlueprintFallback.Value.CreateBookPage(topRecipe, bottomRecipe, background);
    }
    
    /// <summary>
    /// Creates a crafting node, prepares a built prefab, sets up recipe ingredients, creates a recipe and registers it with the database.
    /// If the recipe already exists (the object has already been processes) this method ONLY registers the recipe without processing anything.
    /// </summary>
    /// <param name="reg"></param>
    public static void SetupObjectAsCraftingNode(ScrewStructureRegistration reg)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Blueprints.SetupObjectAsCraftingNode(reg);
            return;
        }

        BlueprintFallback.Value.SetupObjectAsCraftingNode(reg);
    }
    
    public static async Task LoadGltfObjectsFromDirectory(string directory = null)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            await driver.Blueprints.LoadGltfObjectsFromDirectory(directory);
            return;
        }

        await BlueprintFallback.Value.LoadGltfObjectsFromDirectory(directory);
    }

    public static int? GetRecipeIdFromName(string name)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Blueprints.GetRecipeIdFromName(name);

        if (!name.Contains('.'))
            return LoaderUtils.HashString(name);

        var split = name.Split('.');
        if (!int.TryParse(split[^1], out var id))
            return LoaderUtils.HashString(name);

        return id;
    }
    
    public static string ToDisplayName(string str)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Blueprints.ToDisplayName(str);

        str = str.Replace("Recipe", "");
        str = str.Split('.')[0];
        return PascalToHumanRegex.Replace(str, m => $"{m.Value[0]} {char.ToLower(m.Value[1])}");
    }
}
