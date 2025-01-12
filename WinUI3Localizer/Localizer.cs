using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace WinUI3Localizer;

public sealed partial class Localizer : ILocalizer, IDisposable
{
    private readonly Options options;

    private readonly DependencyObjectWeakReferences dependencyObjectsReferences = new();

    private readonly Dictionary<string, LanguageDictionary> languageDictionaries = new();

    private readonly List<LocalizationActions.ActionItem> localizationActions = new();

    internal Localizer(Options options)
    {
        this.options = options;

        if (this.options.DisableDefaultLocalizationActions is false)
        {
            this.localizationActions = LocalizationActions.DefaultActions;
        }

        Uids.DependencyObjectUidSet += Uids_DependencyObjectUidSet; ;
    }

    public event EventHandler<LanguageChangedEventArgs>? LanguageChanged;

    private static ILocalizer Instance { get; set; } = NullLocalizer.Instance;

    private static ILogger Logger { get; set; } = NullLogger.Instance;

    private bool IsDisposed { get; set; }

    private LanguageDictionary CurrentDictionary { get; set; } = new("");

    public static ILocalizer Get() => Instance;

    public IEnumerable<string> GetAvailableLanguages()
    {
        try
        {
            return this.languageDictionaries
                .Values
                .Select(x => x.Language)
                .ToArray();
        }
        catch (Exception exception)
        {
            FailedToGetAvailableLanguagesException localizerException = new(innerException: exception);
            Logger.LogError(localizerException, localizerException.Message);
            throw localizerException;
        }
    }

    public string GetCurrentLanguage() => CurrentDictionary.Language;

    public async Task SetLanguage(string language)
    {
        string previousLanguage = CurrentDictionary.Language;

        try
        {
            if (this.languageDictionaries.TryGetValue(
                language,
                out LanguageDictionary? dictionary) is true &&
                dictionary is not null)
            {
                CurrentDictionary = dictionary;
                await LocalizeDependencyObjects();
                OnLanguageChanged(previousLanguage, CurrentDictionary.Language);
                return;
            }
        }
        catch (LocalizerException)
        {
            throw;
        }
        catch (Exception exception)
        {
            FailedToSetLanguageException localizerException = new(previousLanguage, language, message: string.Empty, innerException: exception);
            Logger.LogError(localizerException, localizerException.Message);
            throw localizerException;
        }
    }

    public string GetLocalizedString(string uid)
    {
        try
        {
            if (this.languageDictionaries.TryGetValue(
                GetCurrentLanguage(),
                out LanguageDictionary? dictionary) is true &&
                dictionary?.TryGetItems(
                    uid,
                    out LanguageDictionary.Items? items) is true &&
                    items.LastOrDefault() is LanguageDictionary.Item item)
            {
                return item.Value;
            }
        }
        catch (LocalizerException)
        {
            throw;
        }
        catch (Exception exception)
        {
            FailedToGetLocalizedStringException localizerException = new(uid, innerException: exception);
            Logger.LogError(localizerException, localizerException.Message);
            throw localizerException;
        }

        return this.options.UseUidWhenLocalizedStringNotFound is true
            ? uid
            : string.Empty;
    }

    public IEnumerable<string> GetLocalizedStrings(string uid)
    {
        try
        {
            if (this.languageDictionaries.TryGetValue(
                GetCurrentLanguage(),
                out LanguageDictionary? dictionary) is true &&
                dictionary?.TryGetItems(
                    uid,
                    out LanguageDictionary.Items? items) is true)
            {
                return items.Select(x => x.Value);
            }
        }
        catch (LocalizerException)
        {
            throw;
        }
        catch (Exception exception)
        {
            FailedToGetLocalizedStringException localizerException = new(uid, innerException: exception);
            Logger.LogError(localizerException, localizerException.Message);
            throw localizerException;
        }

        return this.options.UseUidWhenLocalizedStringNotFound is true
            ? new string[] { uid }
            : Array.Empty<string>();
    }

    public LanguageDictionary GetCurrentLanguageDictionary() => CurrentDictionary;

    public IEnumerable<LanguageDictionary> GetLanguageDictionaries() => this.languageDictionaries.Values;

    public void Dispose()
    {
        Dispose(isDisposing: true);
        GC.SuppressFinalize(this);
    }

    internal static void Set(ILocalizer localizer) => Instance = localizer;

    internal void SetLogger(ILogger logger)
    {
        Logger = logger;

        this.dependencyObjectsReferences.DependencyObjectAdded -= DependencyObjectsReferences_DependencyObjectAdded;
        this.dependencyObjectsReferences.DependencyObjectAdded += DependencyObjectsReferences_DependencyObjectAdded;
        this.dependencyObjectsReferences.DependencyObjectRemoved -= DependencyObjectsReferences_DependencyObjectRemoved;
        this.dependencyObjectsReferences.DependencyObjectRemoved += DependencyObjectsReferences_DependencyObjectRemoved;
    }

    internal void AddLanguageDictionary(LanguageDictionary languageDictionary)
    {
        if (this.languageDictionaries.TryGetValue(
            languageDictionary.Language,
            out LanguageDictionary? targetDictionary) is true)
        {
            int previousItemsCount = targetDictionary.GetItemsCount();

            foreach (LanguageDictionary.Item item in languageDictionary.GetItems())
            {
                targetDictionary.AddItem(item);
            }

            Logger.LogInformation("Merged dictionaries. [Language: {Language} Items: {PreviousItemsCount} -> {CurrentItemsCount}]",
                targetDictionary.Language, previousItemsCount, targetDictionary.GetItemsCount());

            return;
        }

        LanguageDictionary newDictionary = new(languageDictionary.Language);

        foreach (LanguageDictionary.Item item in languageDictionary.GetItems())
        {
            newDictionary.AddItem(item);
        }

        this.languageDictionaries.Add(newDictionary.Language, newDictionary);
        Logger.LogInformation("Added new dictionary. [Language: {Language} Items: {ItemsCount}]",
            newDictionary.Language, newDictionary.GetItemsCount());
    }

    internal void AddLocalizationAction(LocalizationActions.ActionItem item)
    {
        this.localizationActions.Add(item);
    }

    internal void RegisterDependencyObject(DependencyObject dependencyObject)
    {
        this.dependencyObjectsReferences.Add(dependencyObject);
        LocalizeDependencyObject(dependencyObject);
    }

    private static void Uids_DependencyObjectUidSet(object? sender, DependencyObject dependencyObject)
    {
        (Localizer.Instance as Localizer)?.RegisterDependencyObject(dependencyObject);
    }

    private static void DependencyObjectsReferences_DependencyObjectAdded(object? sender, DependencyObjectReferenceAddedEventArgs e)
    {
        Logger.LogTrace("Added DependencyObject. [Type: {Type} Total: {Count}]",
            e.AddedItemType,
            e.ItemsTotal);
    }

    private static void DependencyObjectsReferences_DependencyObjectRemoved(object? sender, DependencyObjectReferenceRemovedEventArgs e)
    {
        Logger.LogTrace("Removed DependencyObject. [Type: {Type} Total: {Count}]",
            e.RemovedItemType,
            e.ItemsTotal);
    }

    private static DependencyProperty? GetDependencyProperty(DependencyObject dependencyObject, string dependencyPropertyName)
    {
        Type type = dependencyObject.GetType();

        if (type.GetProperty(
            dependencyPropertyName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy) is PropertyInfo propertyInfo &&
            propertyInfo.GetValue(null) is DependencyProperty property)
        {
            return property;
        }
        else if (type.GetField(
            dependencyPropertyName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy) is FieldInfo fieldInfo &&
            fieldInfo.GetValue(null) is DependencyProperty field)
        {
            return field;
        }

        return null;
    }

    private void LocalizeDependencyObjectsWithoutDependencyProperty(DependencyObject dependencyObject, string value)
    {
        foreach (LocalizationActions.ActionItem item in this.localizationActions
            .Where(x => x.TargetType == dependencyObject.GetType()))
        {
            item.Action(new LocalizationActions.ActionArguments(dependencyObject, value));
        }
    }
    private async Task LocalizeDependencyObjects()
    {
        foreach (DependencyObject dependencyObject in await this.dependencyObjectsReferences.GetDependencyObjects())
        {
            LocalizeDependencyObject(dependencyObject);
        }
    }

    private void LocalizeDependencyObject(DependencyObject dependencyObject)
    {
        if (Uids.GetUid(dependencyObject) is string uid &&
            CurrentDictionary.TryGetItems(uid, out LanguageDictionary.Items? items) is true)
        {
            foreach (LanguageDictionary.Item item in items)
            {
                if (GetDependencyProperty(
                    dependencyObject,
                    item.DependencyPropertyName) is DependencyProperty dependencyProperty)
                {
                    dependencyObject.SetValue(dependencyProperty, item.Value);
                }
                else
                {
                    LocalizeDependencyObjectsWithoutDependencyProperty(dependencyObject, item.Value);
                }
            }
        }
    }

    private void OnLanguageChanged(string previousLanguage, string currentLanguage)
    {
        LanguageChanged?.Invoke(this, new LanguageChangedEventArgs(previousLanguage, currentLanguage));
        Logger.LogInformation("Changed language. [{PreviousLanguage} -> {CurrentLanguage}]", previousLanguage, currentLanguage);
    }

    private void Dispose(bool isDisposing)
    {
        if (IsDisposed is not true && isDisposing is true)
        {
            this.dependencyObjectsReferences.Dispose();
            IsDisposed = true;
        }
    }
}