using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class Migration2LocalizationTests
{
    private static readonly string[] StoreConsentBodyKeys =
    {
        "Migration2_Consent", "Migration2_PrepareStepBody", "Migration2_RemoveStepBody",
        "Migration2_FinishStepBody", "Migration2_ConsentHint", "Migration2_ConsentWarning"
    };

    private static readonly string[] WizardHeadingKeys =
    {
        "Migration2_PrepareStepTitle", "Migration2_RemoveStepTitle", "Migration2_FinishStepTitle",
        "Migration2_ConsentWarningTitle", "Migration2_CloseInnoTitle",
        "Migration2_CloseInnoWarningTitle", "Migration2_RemovalTitle", "Migration2_RemovalWarningTitle"
    };

    private static readonly string[] InnoPromotionKeys =
    {
        "Migration2_InnoRecommendation.Text", "Migration2_InnoCardTitle.Text",
        "Migration2_InnoCardDescription.Text", "Migration2_InnoAction",
        "Migration2_InnoConsentTitle"
    };

    [Theory]
    [InlineData("en-us", "Microsoft Store opens", "close this app",
        "without asking again", "settings, gateway, files, and models stay where they are")]
    [InlineData("fr-fr", "Le Microsoft Store s'ouvre", "fermer cette application",
        "sans redemander votre accord",
        "paramètres, votre passerelle, vos fichiers et vos modèles restent en place")]
    [InlineData("nl-nl", "De Microsoft Store wordt geopend", "deze app worden gesloten",
        "zonder opnieuw om toestemming te vragen",
        "instellingen, gateway, bestanden en modellen blijven op hun plaats")]
    [InlineData("pt-br", "A Microsoft Store será aberta", "fechar este aplicativo",
        "sem pedir autorização novamente",
        "configurações, gateway, arquivos e modelos permanecem onde estão")]
    [InlineData("zh-cn", "将打开 Microsoft Store", "关闭此应用",
        "无需再次征求您的同意", "您的设置、网关、文件和模型将保留在原处")]
    [InlineData("zh-tw", "將開啟 Microsoft Store", "關閉此應用程式",
        "無需再次徵求您的同意", "您的設定、閘道、檔案和模型將保留在原處")]
    public void InnoDialog_DisclosesSafetyBoundariesInEveryLocale(
        string locale, string storeOpens, string closesThisApp,
        string noRepeatedPrompt, string retainedData)
    {
        var resources = ReadResources(locale);
        var consent = resources["Migration2_InnoConsent"];
        // The dialog discloses the handoff grant and what it does not touch. Detailed
        // removal and finalization guidance is delivered by the Store wizard at the step
        // where the user can act on it, so it is deliberately absent here.
        foreach (var disclosure in new[] { storeOpens, closesThisApp, noRepeatedPrompt, retainedData })
            Assert.Contains(disclosure, consent);

        Assert.False(string.IsNullOrWhiteSpace(resources["Migration2_InnoConsentTitle"]));
        // Chinese locales use the fullwidth question mark.
        Assert.Contains(resources["Migration2_InnoConsentTitle"],
            title => title is '?' or '\uFF1F');
        Assert.NotEqual(resources["Migration2_InnoAction"], resources["Migration2_InnoConsentTitle"]);
        foreach (var nativeChoice in new[] { resources["Migration_StoreYes"], resources["Migration_StoreNo"] })
        {
            Assert.DoesNotContain(nativeChoice + ":", consent);
            Assert.DoesNotContain(nativeChoice + "\uFF1A", consent);
        }
        Assert.DoesNotContain("{0}", consent);
        Assert.DoesNotContain("{1}", consent);
        Assert.DoesNotContain("\u2014", consent);
    }

    [Theory]
    [InlineData("en-us", "settings, gateway, files, and models where they are",
        "safely close the previous app", "check your installation and settings",
        "protected migration records", "When prompted, uninstall it manually",
        "Nothing is uninstalled automatically", "verify removal before starting the Store version",
        "without starting migration", "If validation succeeds", "can no longer start normally")]
    [InlineData("fr-fr", "paramètres, votre passerelle, vos fichiers et vos modèles en place",
        "fermerons l'application précédente en toute sécurité",
        "vérifierons votre installation et vos paramètres",
        "données de migration protégées", "Lorsque vous y serez invité, désinstallez-la manuellement",
        "Rien ne sera désinstallé automatiquement", "vérifions la désinstallation avant de démarrer la version du Store",
        "sans démarrer la migration", "Si la validation réussit", "ne pourra plus démarrer normalement")]
    [InlineData("nl-nl", "instellingen, gateway, bestanden en modellen op hun huidige plek",
        "sluiten de vorige app veilig af", "controleren uw installatie en instellingen",
        "beveiligde migratiegegevens", "Verwijder de app handmatig wanneer daarom wordt gevraagd",
        "Er wordt niets automatisch verwijderd", "verifiëren de verwijdering voordat we de Store-versie starten",
        "zonder de migratie te starten", "Als de validatie slaagt", "niet meer normaal starten")]
    [InlineData("pt-br", "configurações, gateway, arquivos e modelos onde estão",
        "fechar o aplicativo anterior com segurança", "verificar sua instalação e configurações",
        "registros protegidos de migração", "Quando solicitado, desinstale-o manualmente",
        "Nada será desinstalado automaticamente", "Verificamos a remoção antes de iniciar a versão da Store",
        "sem iniciar a migração", "Se a validação for bem-sucedida", "não poderá mais iniciar normalmente")]
    [InlineData("zh-cn", "设置、网关、文件和模型保留在原处", "安全关闭旧版应用", "检查您的安装和设置",
        "受保护的迁移记录", "收到提示后，请手动卸载", "不会自动卸载任何应用",
        "验证移除状态后再启动 Store 版本", "不会开始迁移", "如果验证成功", "将无法正常启动")]
    [InlineData("zh-tw", "設定、閘道、檔案和模型保留在原處", "安全關閉舊版應用程式", "檢查您的安裝和設定",
        "受保護的移轉記錄", "收到提示後，請手動解除安裝", "不會自動解除安裝任何應用程式",
        "驗證移除狀態後再啟動 Store 版本", "不會開始移轉", "如果驗證成功", "將無法正常啟動")]
    public void StoreComposedConsent_DisclosesSafetyAtTheRelevantStep(
        string locale, string retainedData, string gracefulClose, string validation,
        string protectedRecords, string promptedManualRemoval, string noAutomaticUninstall,
        string verifiedRemoval, string cancelWithoutMigration, string validationCondition, string startupBlock)
    {
        var resources = ReadResources(locale);
        Assert.Contains(retainedData, resources["Migration2_Consent"]);
        Assert.Contains(gracefulClose, resources["Migration2_PrepareStepBody"]);
        Assert.Contains(validation, resources["Migration2_PrepareStepBody"]);
        Assert.Contains(protectedRecords, resources["Migration2_PrepareStepBody"]);
        Assert.Contains(promptedManualRemoval, resources["Migration2_RemoveStepBody"]);
        Assert.Contains(noAutomaticUninstall, resources["Migration2_RemoveStepBody"]);
        Assert.Contains(verifiedRemoval, resources["Migration2_FinishStepBody"]);
        Assert.Contains(resources["Migration_StoreNotNow"], resources["Migration2_ConsentHint"]);
        Assert.Contains(cancelWithoutMigration, resources["Migration2_ConsentHint"]);
        Assert.Contains(validationCondition, resources["Migration2_ConsentWarning"]);
        Assert.Contains(startupBlock, resources["Migration2_ConsentWarning"]);
    }

    [Theory]
    [InlineData("en-us", "tray menu", "Don't force-close or uninstall it yet",
        "We'll tell you when it's safe to uninstall", "uninstall", "verify", "finish")]
    [InlineData("fr-fr", "menu dans la zone de notification",
        "Ne forcez pas sa fermeture et ne la désinstallez pas encore",
        "Nous vous indiquerons quand vous pourrez la désinstaller en toute sécurité",
        "désinstall", "vérifi", "terminer")]
    [InlineData("nl-nl", "menu in het systeemvak",
        "Forceer het afsluiten niet en verwijder de app nog niet",
        "We laten u weten wanneer u de app veilig kunt verwijderen", "verwijder", "verifi", "ronden")]
    [InlineData("pt-br", "menu na bandeja do sistema",
        "Não force o encerramento nem desinstale o aplicativo ainda",
        "Avisaremos quando for seguro desinstalar", "desinstal", "verificar", "finalizar")]
    [InlineData("zh-cn", "托盘菜单", "暂时不要强制关闭或卸载该应用",
        "我们会在可以安全卸载时通知您", "卸载", "验证", "完成")]
    [InlineData("zh-tw", "系統匣選單", "暫時不要強制關閉或解除安裝該應用程式",
        "我們會在可以安全解除安裝時通知您", "解除安裝", "驗證", "完成")]
    public void CloseStep_OnlyRequestsGracefulCloseAndRetry(
        string locale, string trayMenu, string noForcedExit, string waitForPermission,
        string uninstall, string verify, string finish)
    {
        var resources = ReadResources(locale);
        var close = resources["Migration2_CloseInno"];
        var warning = resources["Migration2_CloseInnoWarning"];
        Assert.Contains("OpenClaw Companion", close);
        Assert.Contains(trayMenu, close);
        Assert.Contains(resources["Migration_StoreRetry"], close);
        Assert.Contains(noForcedExit, warning);
        Assert.Contains(waitForPermission, warning);
        Assert.DoesNotContain(uninstall, close, StringComparison.OrdinalIgnoreCase);
        foreach (var futureAction in new[] { verify, finish })
        {
            Assert.DoesNotContain(futureAction, close, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(futureAction, warning, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("en-us", "setup has been validated", "migration completion recorded",
        "Installed apps", "uninstall only the previous OpenClaw Companion app", "Keep the Store version",
        "Return here", "verify removal and finish", "settings, gateway, files, and models stay in place",
        "Don't delete migration data or run gateway cleanup")]
    [InlineData("fr-fr", "configuration a été validée", "fin de la migration enregistrée",
        "Applications installées", "désinstallez uniquement l'application OpenClaw Companion précédente",
        "Gardez la version du Store", "Revenez ici", "vérifier la désinstallation et terminer",
        "paramètres, votre passerelle, vos fichiers et vos modèles restent en place",
        "Ne supprimez pas les données de migration et ne lancez pas le nettoyage de la passerelle")]
    [InlineData("nl-nl", "configuratie is gevalideerd", "voltooiing van de migratie is vastgelegd",
        "Geïnstalleerde apps", "verwijder alleen de vorige OpenClaw Companion-app", "Behoud de Store-versie",
        "Keer hier terug", "de verwijdering te verifiëren en af te ronden",
        "instellingen, gateway, bestanden en modellen blijven op hun plaats",
        "Verwijder geen migratiegegevens en voer geen gatewayopruiming uit")]
    [InlineData("pt-br", "configuração foi validada", "conclusão da migração foi registrada",
        "Aplicativos instalados", "desinstale apenas o aplicativo OpenClaw Companion anterior",
        "Mantenha a versão da Store", "Volte aqui", "verificar a remoção e finalizar",
        "configurações, gateway, arquivos e modelos permanecem no mesmo local",
        "Não exclua os dados de migração nem execute a limpeza do gateway")]
    [InlineData("zh-cn", "配置已通过验证", "迁移完成状态已记录",
        "已安装的应用", "仅卸载旧版 OpenClaw Companion 应用", "请保留 Store 版本",
        "返回此处", "验证移除状态并完成迁移", "设置、网关、文件和模型将保留在原处",
        "请勿删除迁移数据或运行网关清理")]
    [InlineData("zh-tw", "設定已通過驗證", "移轉完成狀態已記錄",
        "已安裝的應用程式", "僅解除安裝舊版 OpenClaw Companion 應用程式", "請保留 Store 版本",
        "返回此處", "驗證移除狀態並完成移轉", "設定、閘道、檔案和模型將保留在原處",
        "請勿刪除移轉資料或執行閘道清理")]
    public void RemovalStep_HasTwoOrderedActionsAfterValidationAndReceipt(
        string locale, string validated, string receipt, string installedApps,
        string onlyPreviousApp, string keepStore, string returnHere,
        string verifyAndFinish, string retainedData, string noCleanup)
    {
        var resources = ReadResources(locale);
        var paragraphs = Regex.Split(resources["Migration2_AwaitingRemoval"].Trim(), @"\r?\n\s*\r?\n");
        Assert.Equal(4, paragraphs.Length);
        Assert.Contains(validated, paragraphs[0]);
        Assert.Contains(receipt, paragraphs[0]);
        Assert.StartsWith("1. ", paragraphs[1]);
        Assert.Contains(installedApps, paragraphs[1]);
        Assert.Contains(onlyPreviousApp, paragraphs[1]);
        Assert.Contains(keepStore, paragraphs[1]);
        Assert.StartsWith("2. ", paragraphs[2]);
        Assert.Contains(returnHere, paragraphs[2]);
        Assert.Contains(resources["Migration_StoreRetry"], paragraphs[2]);
        Assert.Contains(verifyAndFinish, paragraphs[2]);
        Assert.Contains(retainedData, paragraphs[3]);
        Assert.Equal(2, Regex.Matches(resources["Migration2_AwaitingRemoval"], @"(?m)^\d+\. ").Count);
        Assert.Contains(noCleanup, resources["Migration2_RemovalWarning"]);
    }

    [Theory]
    [InlineData("en-us")]
    [InlineData("fr-fr")]
    [InlineData("nl-nl")]
    [InlineData("pt-br")]
    [InlineData("zh-cn")]
    [InlineData("zh-tw")]
    public void WizardHeadingsBodiesAndWarnings_ArePresentAndLocalized(string locale)
    {
        var resources = ReadResources(locale);
        var english = ReadResources("en-us");
        foreach (var key in WizardHeadingKeys.Concat(StoreConsentBodyKeys).Concat(new[]
        {
            "Migration2_CloseInno", "Migration2_CloseInnoWarning",
            "Migration2_AwaitingRemoval", "Migration2_RemovalWarning"
        }))
        {
            Assert.False(string.IsNullOrWhiteSpace(resources[key]), key);
            Assert.DoesNotContain("\u2014", resources[key]);
            Assert.DoesNotContain("{0}", resources[key]);
            Assert.DoesNotContain("{1}", resources[key]);
            if (locale != "en-us")
                Assert.NotEqual(english[key], resources[key]);
        }
    }

    [Theory]
    [InlineData("en-us")]
    [InlineData("fr-fr")]
    [InlineData("nl-nl")]
    [InlineData("pt-br")]
    [InlineData("zh-cn")]
    [InlineData("zh-tw")]
    public void InnoPromotion_IsPresentAndLocalized(string locale)
    {
        var resources = ReadResources(locale);
        var english = ReadResources("en-us");
        foreach (var key in InnoPromotionKeys)
        {
            Assert.False(string.IsNullOrWhiteSpace(resources[key]), key);
            Assert.DoesNotContain("\u2014", resources[key]);
            if (locale != "en-us")
                Assert.NotEqual(english[key], resources[key]);
        }
    }

    [Fact]
    public void EnglishWizardCopy_StaysWithinWordBudgetsExcludingHeadings()
    {
        var resources = ReadResources("en-us");
        AssertWordBudget(110, StoreConsentBodyKeys);
        AssertWordBudget(40, "Migration2_CloseInno", "Migration2_CloseInnoWarning");
        AssertWordBudget(75, "Migration2_AwaitingRemoval", "Migration2_RemovalWarning");

        void AssertWordBudget(int maximum, params string[] keys)
        {
            var words = Regex.Matches(string.Join(" ", keys.Select(key => resources[key])), @"\S+").Count;
            Assert.InRange(words, 1, maximum);
        }
    }

    [Fact]
    public void EnglishShippingGuidance_DoesNotRequireReopeningStoreOrUsePreviewCopy()
    {
        var resources = ReadResources("en-us");
        foreach (var key in StoreConsentBodyKeys.Concat(WizardHeadingKeys).Concat(new[]
        {
            "Migration2_InnoConsent", "Migration2_CloseInno", "Migration2_CloseInnoWarning",
            "Migration2_AwaitingRemoval", "Migration2_RemovalWarning"
        }))
        {
            Assert.DoesNotContain("reopen", resources[key], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("preview", resources[key], StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RecoveryGuidance_NamesTheRemedyInsteadOfForbiddingIt()
    {
        var recovery = ReadResources("en-us")["Migration2_Recovery"];
        // The previous copy forbade the only remedy that works, which left recovery a dead end.
        Assert.DoesNotContain("Do not uninstall the previous app", recovery);
        Assert.Contains("Installed apps", recovery);
        Assert.Contains("Retry", recovery);
    }

    [Theory]
    [InlineData("en-us")]
    [InlineData("fr-fr")]
    [InlineData("nl-nl")]
    [InlineData("pt-br")]
    [InlineData("zh-cn")]
    [InlineData("zh-tw")]
    public void DiscardAction_IsNamedInEveryLocale(string locale) =>
        Assert.False(string.IsNullOrWhiteSpace(ReadResources(locale)["Migration2_DiscardRecords"]));

    private static Dictionary<string, string> ReadResources(string locale) =>        XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"))
            .Root!.Elements("data").ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")!.Value,
                StringComparer.Ordinal);
}
