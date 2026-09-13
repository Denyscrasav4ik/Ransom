using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Ransom;

[BepInPlugin("denyscrasav4ik.thedumbfactory.ransom", "Ransom", "1.1.0")]
public class RansomPlugin : BaseUnityPlugin
{
    public static RansomPlugin Instance { get; private set; } = null!;
    public enum RansomState { Inactive, Sequence, RansomActive, ThankYou }
    public RansomState CurrentState { get; private set; } = RansomState.Inactive;
    public bool IsUnderRansom => CurrentState == RansomState.RansomActive;

    public ConfigEntry<float> MinSpawnInterval { get; private set; } = null!;
    public ConfigEntry<float> MaxSpawnInterval { get; private set; } = null!;
    public ConfigEntry<float> MinRansomTime { get; private set; } = null!;
    public ConfigEntry<float> MaxRansomTime { get; private set; } = null!;
    public ConfigEntry<int> MinRansomPoints { get; private set; } = null!;
    public ConfigEntry<int> MaxRansomPoints { get; private set; } = null!;
    public ConfigEntry<bool> NonLethalMode { get; private set; } = null!;

    float ransomTimeLimit, spawnTimer, ransomTimer, tauntSpawnTimer, displayedRansomPoints;
    int requiredRansomPoints, currentDownloadSegment, currentRansomPoints, currentMusicLayer;
    bool ransomPaymentComplete;

    const float StaticInterval = .04f, FlickerInterval = .04f, VignetteInterval = .1f, HudInterval = .06f;
    const int DownloadSegments = 10;
    const int EffectFrameCount = 30;
    const float SpawnIdleTime = .5f, DownloadHudScale = 1.4f, WindowAnimationDuration = 0.2f, WindowStartScale = 0.05f;
    const float RansomCounterSmoothSpeed = 30f;

    Coroutine staticCoroutine = null!, vignetteCoroutine = null!, wiggleCoroutine = null!, teleportMainCoroutine = null!, inventoryRestoreCoroutine = null!;

    GameObject downloadHud = null!, ransomDemandWindow = null!;
    Text downloadText = null!, ransomDemandTitle = null!, ransomDemandAmount = null!, ransomDemandTimer = null!;
    Image[] downloadSegments = null!;
    Image downloadProgressGlow = null!, ransomDemandBackground = null!, ransomDemandIdleIcon = null!;

    Canvas ransomCanvas = null!;
    Image warningImage = null!, staticOverlay = null!, vignetteOverlay = null!, thankYouImage = null!, okSignImage = null!;

    readonly List<GameObject> activeTauntWindows = new();
    readonly Dictionary<int, Sprite> originalItemSprites = new();

    SoundObject sndAttack = null!, sndCash = null!, sndInstall = null!, sndSpawn = null!, sndTauntLeave = null!, sndTauntSpawn = null!, sndThankYou = null!;
    LoopingSoundObject sndLayer1 = null!, sndLayer2 = null!, sndLayer3 = null!;

    public Sprite stopSignSprite = null!, attackSprite = null!, idleSprite = null!, thankYouTextSprite = null!, okSignSprite = null!;
    public List<Sprite> tauntSprites = new();

    readonly Sprite[] staticFrames = new Sprite[EffectFrameCount];
    readonly Sprite[] vignetteFrames = new Sprite[EffectFrameCount];

    int staticFrameIndex;
    int vignetteFrameIndex;

    Vector3 warningOriginalScale, downloadHudOriginalScale;
    Vector2 warningOriginalSizeDelta, warningOriginalAnchoredPosition;

    void Awake()
    {
        Instance = this;

        MinSpawnInterval = Config.Bind("Ransom", "MinSpawnInterval", 45f, "Minimum time in seconds before Ransom can spawn.");
        MaxSpawnInterval = Config.Bind("Ransom", "MaxSpawnInterval", 90f, "Maximum time in seconds before Ransom can spawn.");
        MinRansomTime = Config.Bind("Ransom", "MinRansomTime", 45f, "Minimum amount of time in seconds the player has to pay the ransom.");
        MaxRansomTime = Config.Bind("Ransom", "MaxRansomTime", 60f, "Maximum amount of time in seconds the player has to pay the ransom.");
        MinRansomPoints = Config.Bind("Ransom", "MinRansomPoints", 25, "Minimum points required by Ransom.");
        MaxRansomPoints = Config.Bind("Ransom", "MaxRansomPoints", 50, "Maximum points required by Ransom.");
        NonLethalMode = Config.Bind("Ransom", "NonLethalMode", false, "If true, failing to pay the ransom removes all player items instead of ending the game.");

        ValidateConfig();

        LoadAssets();
        GenerateEffectFrames();

        new Harmony("denyscrasav4ik.thedumbfactory.ransom").PatchAll();
        CurrentState = RansomState.Inactive;
        spawnTimer = UnityEngine.Random.Range(MinSpawnInterval.Value, MaxSpawnInterval.Value);
    }

    void ValidateConfig()
    {
        MinSpawnInterval.Value = Mathf.Max(0f, MinSpawnInterval.Value);
        MaxSpawnInterval.Value = Mathf.Max(MinSpawnInterval.Value, MaxSpawnInterval.Value);
        MinRansomTime.Value = Mathf.Max(1f, MinRansomTime.Value);
        MaxRansomTime.Value = Mathf.Max(MinRansomTime.Value, MaxRansomTime.Value);
        MinRansomPoints.Value = Mathf.Max(1, MinRansomPoints.Value);
        MaxRansomPoints.Value = Mathf.Max(MinRansomPoints.Value, MaxRansomPoints.Value);
    }

    void LoadAssets()
    {
        string bundleName =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Ransom.Resources.assets-win.bundle" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Ransom.Resources.assets-mac.bundle" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Ransom.Resources.assets-linux.bundle" :
            throw new PlatformNotSupportedException();

        using Stream stream = typeof(RansomPlugin).Assembly.GetManifestResourceStream(bundleName)!;
        AssetBundle bundle = AssetBundle.LoadFromStream(stream);

        sndAttack = bundle.LoadAsset<SoundObject>("Ransom_Attack");
        sndCash = bundle.LoadAsset<SoundObject>("Ransom_Cash");
        sndInstall = bundle.LoadAsset<SoundObject>("Ransom_Install");
        sndSpawn = bundle.LoadAsset<SoundObject>("Ransom_Spawn");
        sndTauntLeave = bundle.LoadAsset<SoundObject>("Ransom_TauntLeave");
        sndTauntSpawn = bundle.LoadAsset<SoundObject>("Ransom_TauntSpawn");
        sndThankYou = bundle.LoadAsset<SoundObject>("Ransom_ThankYou");

        sndLayer1 = bundle.LoadAsset<LoopingSoundObject>("Ransom_Layer1");
        sndLayer2 = bundle.LoadAsset<LoopingSoundObject>("Ransom_Layer2");
        sndLayer3 = bundle.LoadAsset<LoopingSoundObject>("Ransom_Layer3");

        attackSprite = bundle.LoadAsset<Sprite>("Ransom_Attack");
        idleSprite = bundle.LoadAsset<Sprite>("Ransom_Idle");
        stopSignSprite = bundle.LoadAsset<Sprite>("Ransom_StopSign");
        thankYouTextSprite = bundle.LoadAsset<Sprite>("Ransom_ThankYouText");
        okSignSprite = bundle.LoadAsset<Sprite>("Ransom_OkSign");

        for (int i = 1; i <= 8; i++)
        {
            Sprite s = bundle.LoadAsset<Sprite>($"Ransom_Taunt{i}");
            if (s) tauntSprites.Add(s);
        }

        bundle.Unload(false);
    }

    float GetSoundLength(SoundObject sound, float fallback) => sound != null && sound.soundClip != null ? sound.soundClip.length : fallback;

    void Update()
    {
        if (Singleton<BaseGameManager>.Instance == null || Singleton<BaseGameManager>.Instance is PitstopGameManager || Singleton<BaseGameManager>.Instance is PlaceholderWinManager)
        {
            if (CurrentState != RansomState.Inactive) ResetEverything();
            return;
        }

        switch (CurrentState)
        {
            case RansomState.Inactive:
                if ((spawnTimer -= Time.deltaTime) <= 0)
                    StartCoroutine(MainRansomSequence());
                break;

            case RansomState.RansomActive:
                ransomTimer -= Time.deltaTime;
                tauntSpawnTimer -= Time.deltaTime;

                if (ransomDemandWindow)
                {
                    float targetRansomPoints = requiredRansomPoints - currentRansomPoints;
                    displayedRansomPoints = Mathf.MoveTowards(displayedRansomPoints, targetRansomPoints, RansomCounterSmoothSpeed * Time.deltaTime);
                    ransomDemandAmount.text = Mathf.CeilToInt(displayedRansomPoints).ToString();

                    int secondsLeft = Mathf.Max(0, Mathf.CeilToInt(ransomTimer));
                    ransomDemandTimer.text = string.Format(LocalizationManager.Instance.GetLocalizedText("Ransom_Time"), secondsLeft / 60, secondsLeft % 60);
                }

                ManageMusicLayers();

                if (tauntSpawnTimer <= 0)
                {
                    SpawnTauntWindow();
                    tauntSpawnTimer = UnityEngine.Random.Range(2.5f, 5f);
                }

                if (ransomTimer <= 0) FailRansom();
                break;
        }
    }

    IEnumerator MainRansomSequence()
    {
        if (ransomCanvas == null)
        {
            InitializeRansomCanvas();

            if (ransomCanvas == null || warningImage == null)
            {
                CurrentState = RansomState.Inactive;
                yield break;
            }
        }

        CurrentState = RansomState.Sequence;
        ResetVisualState();

        warningImage.gameObject.SetActive(true);
        warningImage.sprite = idleSprite;
        warningImage.color = Color.white;
        RestoreWarningImageTransform();
        TeleportRect(warningImage.rectTransform);

        float spawnLen = GetSoundLength(sndSpawn, 1f);
        PlaySound(sndSpawn!);

        yield return new WaitForSeconds(Mathf.Min(SpawnIdleTime, spawnLen));

        staticOverlay.gameObject.SetActive(true);
        staticOverlay.color = Color.white;
        StartStaticAnimation();

        warningImage.rectTransform.anchoredPosition = Vector2.zero;
        RestoreWarningImageTransform();
        warningImage.sprite = stopSignSprite;

        if (spawnLen > SpawnIdleTime)
        {
            Vector3 mouseStart = Input.mousePosition;
            float remaining = spawnLen - SpawnIdleTime, elapsed = 0f;
            bool responded = Input.anyKey || Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);

            while (elapsed < remaining)
            {
                if (!responded && (Input.anyKeyDown || Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2) || Input.mousePosition != mouseStart))
                    responded = true;

                elapsed += Time.deltaTime;
                yield return null;
            }

            if (!responded)
            {
                ResetEverything();
                yield break;
            }
        }

        warningImage.sprite = attackSprite;
        RestoreWarningImageTransform();
        ScaleImageToScreen(warningImage);

        float attackLen = GetSoundLength(sndAttack, 2f);
        PlaySound(sndInstall!);

        Coroutine flicker = StartCoroutine(RedWhiteFlicker(attackLen));
        yield return new WaitForSeconds(attackLen / 2f);

        float installLen = Mathf.Max(0f, GetSoundLength(sndInstall, 4f) - 2.751f);
        SetupDownloadHUD();
        yield return SyncDownloadHud(installLen - 1.25f);

        if (flicker != null) StopCoroutine(flicker);

        warningImage.gameObject.SetActive(false);
        StopStaticAnimation();
        staticOverlay.gameObject.SetActive(false);

        if (downloadHud)
        {
            downloadHud.transform.localScale = downloadHudOriginalScale;
            downloadHud.SetActive(false);
        }

        TriggerRansomActivePhase();
    }

    IEnumerator RedWhiteFlicker(float duration)
    {
        for (float t = 0; t < duration; t += FlickerInterval)
        {
            warningImage.color = UnityEngine.Random.value > .5f ? Color.red : Color.white;
            yield return new WaitForSeconds(FlickerInterval);
        }
        warningImage.color = Color.white;
    }

    IEnumerator SyncDownloadHud(float duration)
    {
        float elapsed = 0;
        while (elapsed < duration)
        {
            float progress = duration <= 0 ? 1f : elapsed / duration;
            currentDownloadSegment = Mathf.Clamp(Mathf.FloorToInt(progress * DownloadSegments), 0, DownloadSegments);
            downloadHud.transform.localScale = Vector3.one * Mathf.Lerp(1f, DownloadHudScale, progress);

            UpdateDownloadHUD(progress);
            yield return new WaitForSeconds(HudInterval);
            elapsed += HudInterval;
        }

        currentDownloadSegment = DownloadSegments;
        UpdateDownloadHUD(1f);
        downloadHud.transform.localScale = downloadHudOriginalScale;
    }

    void PlaySound(SoundObject sound)
    {
        if (sound && CoreGameManager.Instance)
            CoreGameManager.Instance.audMan.PlaySingle(sound);
    }

    void PlayMusicLayer(LoopingSoundObject layer, bool loop)
    {
        if (layer && Singleton<MusicManager>.Instance)
        {
            Singleton<MusicManager>.Instance.StopFile();
            Singleton<MusicManager>.Instance.QueueFile(layer, loop);
        }
    }

    void StopMusic()
    {
        if (Singleton<MusicManager>.Instance)
            Singleton<MusicManager>.Instance.StopFile();
        currentMusicLayer = 0;
    }

    public void InitializeRansomCanvas()
    {
        if (ransomCanvas != null) return;
        SetupRansomCanvas();

        warningOriginalScale = warningImage.rectTransform.localScale;
        warningOriginalSizeDelta = warningImage.rectTransform.sizeDelta;
        warningOriginalAnchoredPosition = warningImage.rectTransform.anchoredPosition;
        downloadHudOriginalScale = downloadHud.transform.localScale;

        ResetEverything();
    }

    void StartStaticAnimation()
    {
        StopStaticAnimation();
        staticCoroutine = StartCoroutine(StaticAnimation());
    }

    void StopStaticAnimation()
    {
        if (staticCoroutine != null)
        {
            StopCoroutine(staticCoroutine);
            staticCoroutine = null!;
        }
    }

    IEnumerator StaticAnimation()
    {
        staticFrameIndex = UnityEngine.Random.Range(0, EffectFrameCount);
        while (true)
        {
            staticOverlay.sprite = staticFrames[staticFrameIndex];

            staticFrameIndex++;
            if (staticFrameIndex >= EffectFrameCount)
                staticFrameIndex = 0;

            yield return new WaitForSeconds(StaticInterval);
        }
    }

    void SetupDownloadHUD()
    {
        if (!downloadHud) CreateDownloadHUD();

        warningImage.gameObject.SetActive(false);
        ransomDemandWindow.SetActive(false);
        vignetteOverlay.gameObject.SetActive(false);

        currentDownloadSegment = 0;
        downloadHud.SetActive(true);
        downloadHud.GetComponent<RectTransform>().anchoredPosition = Vector2.zero;
        downloadHud.transform.localScale = downloadHudOriginalScale;

        foreach (Image i in downloadSegments)
            i.color = new Color(.01f, .005f, .005f);

        downloadText.text = LocalizationManager.Instance.GetLocalizedText("Ransom_Downloading");
        downloadText.color = Color.white;
        downloadProgressGlow.rectTransform.sizeDelta = new Vector2(0, 36);
    }

    void CreateDownloadHUD()
    {
        downloadHud = new GameObject("DownloadHUD", typeof(RectTransform));
        downloadHud.transform.SetParent(ransomCanvas.transform, false);

        RectTransform hud = downloadHud.GetComponent<RectTransform>();
        hud.sizeDelta = new Vector2(720, 150);

        downloadText = CreateText("DownloadText", downloadHud.transform, LocalizationManager.Instance.GetLocalizedText("Ransom_Downloading"), 36, Color.white);
        downloadText.fontStyle = FontStyle.Bold;
        downloadText.alignment = TextAnchor.MiddleCenter;
        var outline = downloadText.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(1f, 0f, 0f, 1f);
        outline.effectDistance = new Vector2(2f, -2f);
        downloadText.rectTransform.sizeDelta = new Vector2(720, 50);
        downloadText.rectTransform.anchoredPosition = new Vector2(0, 45);

        Image outer = CreateImage("DownloadOuterBorder", downloadHud.transform);
        outer.color = new Color(0, 0, 0, .95f);
        outer.rectTransform.sizeDelta = new Vector2(700, 48);
        outer.rectTransform.anchoredPosition = new Vector2(0, -20);

        Image inner = CreateImage("DownloadInnerBorder", outer.transform);
        inner.color = new Color(.35f, 0, 0);
        SetFullRect(inner.rectTransform, 3);

        downloadProgressGlow = CreateImage("ProgressGlow", inner.transform);
        downloadProgressGlow.color = new Color(1, 0, 0, .12f);
        SetBarRect(downloadProgressGlow.rectTransform, 36);

        GameObject container = new GameObject("DownloadSegments", typeof(RectTransform));
        container.transform.SetParent(inner.transform, false);
        SetFullRect(container.GetComponent<RectTransform>());

        downloadSegments = new Image[DownloadSegments];
        float width = 64, gap = 6, total = DownloadSegments * width + (DownloadSegments - 1) * gap;
        float start = -total / 2 + width / 2;

        for (int i = 0; i < DownloadSegments; i++)
        {
            Image segment = CreateImage($"DownloadSegment{i}", container.transform);
            segment.color = new Color(.01f, .005f, .005f);
            segment.rectTransform.sizeDelta = new Vector2(width, 30);
            segment.rectTransform.anchoredPosition = new Vector2(start + i * (width + gap), 0);
            downloadSegments[i] = segment;
        }

        downloadHudOriginalScale = downloadHud.transform.localScale;
        downloadHud.SetActive(false);
    }

    void UpdateDownloadHUD(float progress)
    {
        if (!downloadHud) return;

        string dots = new string('.', Time.frameCount % 4);
        downloadText.text = LocalizationManager.Instance.GetLocalizedText("Ransom_Downloading") + dots;
        downloadText.color = UnityEngine.Random.value < .25f ? new Color(1f, .03f, .03f) : Color.white;
        downloadProgressGlow.rectTransform.sizeDelta = new Vector2(680 * progress, 36);

        float glow = .08f + Mathf.Sin(Time.time * 12) * .04f;
        downloadProgressGlow.color = new Color(1, 0, 0, Mathf.Clamp01(glow));

        for (int i = 0; i < downloadSegments.Length; i++)
            downloadSegments[i].color = i < currentDownloadSegment
                ? new Color(UnityEngine.Random.Range(.75f, 1f), .015f, .015f)
                : new Color(.01f, .005f, .005f);

        downloadHud.GetComponent<RectTransform>().anchoredPosition =
            new Vector2(UnityEngine.Random.Range(-3f, 3f), UnityEngine.Random.Range(-2f, 2f));
    }

    void TriggerRansomActivePhase()
    {
        CurrentState = RansomState.RansomActive;
        ransomTimeLimit = UnityEngine.Random.Range(MinRansomTime.Value, MaxRansomTime.Value);
        requiredRansomPoints = UnityEngine.Random.Range(MinRansomPoints.Value, MaxRansomPoints.Value + 1);
        ransomTimer = ransomTimeLimit;
        currentRansomPoints = 0;
        displayedRansomPoints = requiredRansomPoints;
        tauntSpawnTimer = 1;
        ransomPaymentComplete = false;

        warningImage.gameObject.SetActive(false);
        RestoreWarningImageTransform();

        if (downloadHud)
        {
            downloadHud.transform.localScale = downloadHudOriginalScale;
            downloadHud.SetActive(false);
        }

        staticOverlay.gameObject.SetActive(false);
        vignetteOverlay.gameObject.SetActive(true);

        vignetteCoroutine = StartCoroutine(AnimateVignette());
        wiggleCoroutine = StartCoroutine(WiggleWindows());
        teleportMainCoroutine = StartCoroutine(ClearSingleTaunt(ransomDemandWindow, UnityEngine.Random.Range(4f, 10f)));

        ShowRansomDemandWindow();
        ReplaceInventoryIcons();

        currentMusicLayer = 1;
        PlayMusicLayer(sndLayer1, true);
    }

    void GenerateEffectFrames()
    {
        for (int i = 0; i < EffectFrameCount; i++)
        {
            Texture2D staticTexture = GenerateStaticTexture(480, 360);
            staticFrames[i] = Sprite.Create(staticTexture, new Rect(0, 0, staticTexture.width, staticTexture.height), Vector2.one * .5f);

            staticTexture.filterMode = FilterMode.Point;

            Texture2D vignetteTexture = GenerateVignetteTexture(480, 360);
            vignetteFrames[i] = Sprite.Create(vignetteTexture, new Rect(0, 0, vignetteTexture.width, vignetteTexture.height), Vector2.one * .5f);

            vignetteTexture.filterMode = FilterMode.Point;
        }

        staticFrameIndex = 0;
        vignetteFrameIndex = 0;
    }

    IEnumerator AnimateVignette()
    {
        vignetteFrameIndex = UnityEngine.Random.Range(0, EffectFrameCount);

        while (CurrentState == RansomState.RansomActive)
        {
            vignetteOverlay.sprite = vignetteFrames[vignetteFrameIndex];

            vignetteFrameIndex++;
            if (vignetteFrameIndex >= EffectFrameCount)
                vignetteFrameIndex = 0;

            yield return new WaitForSeconds(VignetteInterval);
        }
    }


    IEnumerator WiggleWindows()
    {
        while (CurrentState == RansomState.RansomActive)
        {
            Vector2 offset = new Vector2(UnityEngine.Random.Range(-2f, 2f), UnityEngine.Random.Range(-2f, 2f));
            if (ransomDemandWindow)
                ransomDemandWindow.GetComponent<RectTransform>().anchoredPosition += offset;

            foreach (GameObject tw in activeTauntWindows)
                if (tw) tw.GetComponent<RectTransform>().anchoredPosition += offset;

            yield return new WaitForSeconds(.1f);
        }
    }

    void ShowRansomDemandWindow()
    {
        ResetRansomDemandWindow();
        RectTransform rect = ransomDemandWindow.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(560, 400);
        rect.anchoredPosition = Vector2.zero;

        ransomDemandTitle.text = LocalizationManager.Instance.GetLocalizedText("Ransom_Encrypted");
        ransomDemandAmount.text = " ";
        ransomDemandAmount.color = new Color(1, .81f, .15f);
        ransomDemandBackground.sprite = null;
        ransomDemandBackground.color = Color.white;

        PlaySound(sndTauntSpawn);
        ransomDemandWindow.SetActive(true);

        StartCoroutine(TweenScale(rect, Vector3.one * WindowStartScale, Vector3.one, WindowAnimationDuration, () =>
        {
            if (ransomDemandBackground) ransomDemandBackground.color = new Color(.97f, .08f, .05f);
        }));
    }

    void ManageMusicLayers()
    {
        float elapsed = ransomTimeLimit - ransomTimer;
        if (ransomTimer <= 26f)
        {
            if (currentMusicLayer != 3)
            {
                currentMusicLayer = 3;
                PlayMusicLayer(sndLayer3, false);
            }
        }
        else if (elapsed >= (ransomTimeLimit - 26f) / 2f && currentMusicLayer != 2)
        {
            currentMusicLayer = 2;
            PlayMusicLayer(sndLayer2, true);
        }
    }

    public void AddRansomPoints(int points)
    {
        if (!IsUnderRansom || ransomPaymentComplete) return;

        PlaySound(sndCash);
        int pointsNeeded = requiredRansomPoints - currentRansomPoints;
        int pointsToRansom = Mathf.Min(points, pointsNeeded);
        int leftoverPoints = points - pointsToRansom;

        currentRansomPoints += pointsToRansom;

        if (currentRansomPoints >= requiredRansomPoints)
        {
            currentRansomPoints = requiredRansomPoints;
            ransomPaymentComplete = true;

            if (leftoverPoints > 0 && CoreGameManager.Instance != null)
                CoreGameManager.Instance.AddPoints(leftoverPoints, 0, true, true, false);

            StartCoroutine(ThankYouSequence());
        }
    }

    IEnumerator ThankYouSequence()
    {
        CurrentState = RansomState.ThankYou;
        StopAllRansomCoroutines();
        StopMusic();
        ClearTauntWindows();
        RestoreInventoryIcons(true);

        vignetteOverlay.gameObject.SetActive(false);
        staticOverlay.gameObject.SetActive(false);
        warningImage.gameObject.SetActive(false);
        RestoreWarningImageTransform();

        if (downloadHud) downloadHud.SetActive(false);

        RectTransform rect = ransomDemandWindow.GetComponent<RectTransform>();
        ransomDemandTitle.gameObject.SetActive(false);
        ransomDemandAmount.transform.parent.parent.gameObject.SetActive(false);
        ransomDemandTimer.transform.parent.parent.gameObject.SetActive(false);
        ransomDemandWindow.transform.Find("WarningBoxBorder")?.gameObject.SetActive(false);
        ransomDemandBackground.color = Color.black;

        Vector2 startSize = rect.sizeDelta, targetSize = new Vector2(560 * 1.3f, 350 * 1.3f);
        Vector2 startPos = rect.anchoredPosition;

        yield return new WaitForSeconds(0.2f);

        float elapsed = 0f, duration = 0.3f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;
            rect.sizeDelta = Vector2.Lerp(startSize, targetSize, t);
            rect.anchoredPosition = Vector2.Lerp(startPos, Vector2.zero, t);
            yield return null;
        }

        rect.sizeDelta = targetSize;
        rect.anchoredPosition = Vector2.zero;
        yield return new WaitForSeconds(0.2f);

        ransomDemandBackground.color = new Color(0.12f, 0.65f, 0.05f);
        ransomDemandIdleIcon.gameObject.SetActive(false);
        PlaySound(sndThankYou);

        okSignImage.gameObject.SetActive(true);
        okSignImage.transform.SetParent(ransomDemandWindow.transform, false);
        okSignImage.rectTransform.anchoredPosition = new Vector2(0, -30);
        okSignImage.rectTransform.sizeDelta = new Vector2(194, 194);

        yield return TweenScale(okSignImage.rectTransform, Vector3.one * 0.1f, Vector3.one, 0.3f);
        yield return new WaitForSeconds(0.1f);

        thankYouImage.gameObject.SetActive(true);
        thankYouImage.transform.SetParent(ransomDemandWindow.transform, false);
        thankYouImage.rectTransform.anchoredPosition = new Vector2(0, 110);
        thankYouImage.rectTransform.sizeDelta = new Vector2(488, 87);

        yield return TweenScale(thankYouImage.rectTransform, Vector3.one * 0.1f, Vector3.one, 0.2f);
        yield return new WaitForSeconds(4.0f);

        ResetEverything();
    }

    void FailRansom()
    {
        CurrentState = RansomState.Sequence;
        StartCoroutine(FailureSequence());
    }

    IEnumerator FailureSequence()
    {
        StopAllRansomCoroutines();
        ClearTauntWindows();

        if (ransomDemandWindow) ransomDemandWindow.SetActive(false);
        StopMusic();

        warningImage.gameObject.SetActive(true);
        warningImage.sprite = attackSprite;
        warningImage.color = Color.white;
        RestoreWarningImageTransform();
        ScaleImageToScreen(warningImage);
        vignetteOverlay.gameObject.SetActive(false);
        staticOverlay.gameObject.SetActive(true);
        staticOverlay.color = Color.white;
        StartStaticAnimation();

        float attackLen = Mathf.Max(0f, GetSoundLength(sndAttack, 3f) - 1.758f);
        PlaySound(sndAttack!);

        Coroutine flicker = StartCoroutine(RedWhiteFlicker(attackLen));
        yield return new WaitForSeconds(attackLen);

        if (flicker != null) StopCoroutine(flicker);

        StopStaticAnimation();
        staticOverlay.gameObject.SetActive(false);
        RestoreWarningImageTransform();
        ResetEverything();

        PlayerManager? player = CoreGameManager.Instance?.GetPlayer(0);

        if (NonLethalMode.Value)
        {
            if (player != null && player.itm != null)
                player.itm.ClearItems();

            ResetEverything();
            yield break;
        }

        Baldi baldi = FindObjectOfType<Baldi>();

        if (player != null && baldi != null)
            CoreGameManager.Instance?.EndGame(player.transform, baldi);
        else
            CoreGameManager.Instance?.Quit();
    }

    public void ResetEverything()
    {
        StopAllRansomCoroutines();
        StopMusic();
        RestoreInventoryIcons(false);
        ClearTauntWindows();

        if (warningImage)
        {
            warningImage.gameObject.SetActive(false);
            warningImage.color = Color.white;
            RestoreWarningImageTransform();
        }

        if (staticOverlay) staticOverlay.gameObject.SetActive(false);
        if (vignetteOverlay) vignetteOverlay.gameObject.SetActive(false);
        if (thankYouImage) thankYouImage.gameObject.SetActive(false);
        if (okSignImage) okSignImage.gameObject.SetActive(false);

        if (downloadHud)
        {
            downloadHud.transform.localScale = downloadHudOriginalScale;
            downloadHud.SetActive(false);
        }

        if (ransomDemandWindow && ransomDemandWindow.activeSelf)
            StartCoroutine(AnimateRansomWindowReset());
        else if (ransomDemandWindow)
            ResetRansomDemandWindow();

        CurrentState = RansomState.Inactive;
        ransomTimer = tauntSpawnTimer = currentDownloadSegment = currentRansomPoints = currentMusicLayer = 0;
        ransomPaymentComplete = false;
        spawnTimer = UnityEngine.Random.Range(MinSpawnInterval.Value, MaxSpawnInterval.Value);
    }

    IEnumerator AnimateRansomWindowReset()
    {
        if (!ransomDemandWindow) yield break;

        RectTransform rect = ransomDemandWindow.GetComponent<RectTransform>();
        if (!rect)
        {
            ResetRansomDemandWindow();
            yield break;
        }

        yield return TweenScale(rect, rect.localScale, Vector3.one * WindowStartScale, WindowAnimationDuration);
        ResetRansomDemandWindow();
    }

    void ResetVisualState()
    {
        if (warningImage) warningImage.gameObject.SetActive(false);
        if (staticOverlay) staticOverlay.gameObject.SetActive(false);
        if (vignetteOverlay) vignetteOverlay.gameObject.SetActive(false);
        if (thankYouImage) thankYouImage.gameObject.SetActive(false);
        if (okSignImage) okSignImage.gameObject.SetActive(false);
        if (ransomDemandWindow) ransomDemandWindow.SetActive(false);
        if (downloadHud) downloadHud.SetActive(false);
    }

    void StopAllRansomCoroutines()
    {
        StopStaticAnimation();
        if (vignetteCoroutine != null) { StopCoroutine(vignetteCoroutine); vignetteCoroutine = null!; }
        if (wiggleCoroutine != null) { StopCoroutine(wiggleCoroutine); wiggleCoroutine = null!; }
        if (teleportMainCoroutine != null) { StopCoroutine(teleportMainCoroutine); teleportMainCoroutine = null!; }
    }

    void ReplaceInventoryIcons()
    {
        originalItemSprites.Clear();
        if (CoreGameManager.Instance == null) return;

        HudManager hud = CoreGameManager.Instance.GetHud(0);
        PlayerManager player = CoreGameManager.Instance.GetPlayer(0);
        if (!hud || !player || player.itm == null) return;

        for (int i = 0; i <= player.itm.maxItem; i++)
        {
            ItemObject item = player.itm.items[i];
            if (item == null) continue;

            originalItemSprites[i] = item.itemSpriteSmall;
            hud.UpdateItemIcon(i, stopSignSprite);
        }
    }

    void RestoreInventoryIcons(bool animate)
    {
        if (inventoryRestoreCoroutine != null)
        {
            StopCoroutine(inventoryRestoreCoroutine);
            inventoryRestoreCoroutine = null!;
        }

        if (CoreGameManager.Instance == null)
        {
            originalItemSprites.Clear();
            return;
        }

        HudManager hud = CoreGameManager.Instance.GetHud(0);
        PlayerManager player = CoreGameManager.Instance.GetPlayer(0);

        if (!hud || !player || player.itm == null)
        {
            originalItemSprites.Clear();
            return;
        }

        if (originalItemSprites.Count == 0)
            return;

        if (!animate)
        {
            foreach (var pair in originalItemSprites)
                hud.UpdateItemIcon(pair.Key, pair.Value);

            originalItemSprites.Clear();
            return;
        }

        inventoryRestoreCoroutine = StartCoroutine(RestoreInventoryIconsSequence(hud, player));
    }

    IEnumerator RestoreInventoryIconsSequence(HudManager hud, PlayerManager player)
    {
        foreach (var pair in originalItemSprites)
        {
            if (!hud || !player)
                yield break;

            StartCoroutine(FadeInventoryIcon(hud, pair.Key, pair.Value));

            yield return new WaitForSeconds(0.1f);
        }

        inventoryRestoreCoroutine = null!;
        originalItemSprites.Clear();
    }


    Image? GetInventoryIconImage(HudManager hud, int slot)
    {
        if (!hud || slot < 0)
            return null;

        FieldInfo? field = AccessTools.Field(typeof(HudManager), "itemSprites");
        if (field?.GetValue(hud) is Image[] icons)
        {
            if (slot >= 0 && slot < icons.Length)
                return icons[slot];
        }

        return null;
    }

    IEnumerator FadeInventoryIcon(HudManager hud, int slot, Sprite restoredSprite)
    {
        if (!hud || CoreGameManager.Instance == null || CoreGameManager.Instance.GetPlayer(0) == null)
            yield break;

        Image? icon = GetInventoryIconImage(hud, slot);

        hud.UpdateItemIcon(slot, restoredSprite);

        icon = GetInventoryIconImage(hud, slot);

        if (!icon)
            yield break;

        const float fadeDuration = 0.5f;
        float elapsed = 0f;

        while (elapsed < fadeDuration)
        {
            if (!icon)
                yield break;

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / fadeDuration);

            icon!.color = Color.Lerp(Color.green, Color.white, t);

            yield return null;
        }

        if (icon)
            icon!.color = Color.white;
    }

    void SpawnTauntWindow()
    {
        PlaySound(sndTauntSpawn);

        GameObject obj = new GameObject("TauntWindow", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        obj.transform.SetParent(ransomCanvas.transform, false);

        Image image = obj.GetComponent<Image>();
        RectTransform r = obj.GetComponent<RectTransform>();

        r.sizeDelta = new Vector2(UnityEngine.Random.Range(200, 400), UnityEngine.Random.Range(200, 400));
        TeleportRect(r);

        image.sprite = null;
        image.color = Color.white;
        obj.SetActive(true);

        activeTauntWindows.Add(obj);
        Sprite targetSprite = tauntSprites.Count > 0 ? tauntSprites[UnityEngine.Random.Range(0, tauntSprites.Count)] : null!;

        StartCoroutine(TweenScale(r, Vector3.one * WindowStartScale, Vector3.one, WindowAnimationDuration, () =>
        {
            if (!obj || !image) return;
            if (targetSprite != null) image.sprite = targetSprite;
            else image.color = new Color(UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value, .9f);
        }));

        StartCoroutine(ClearSingleTaunt(obj, UnityEngine.Random.Range(4f, 10f)));
    }

    IEnumerator ClearSingleTaunt(GameObject obj, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (!obj || CurrentState != RansomState.RansomActive) yield break;

        if (obj == ransomDemandWindow)
        {
            TeleportRect(obj.GetComponent<RectTransform>());
            StartCoroutine(ClearSingleTaunt(obj, UnityEngine.Random.Range(4f, 10f)));
        }
        else if (activeTauntWindows.Remove(obj))
        {
            PlaySound(sndTauntLeave);
            StartCoroutine(AnimateTauntWindowOut(obj));
        }
    }

    IEnumerator AnimateTauntWindowOut(GameObject obj)
    {
        if (!obj) yield break;
        Image image = obj.GetComponent<Image>();
        RectTransform rect = obj.GetComponent<RectTransform>();

        if (!image || !rect)
        {
            Destroy(obj);
            yield break;
        }

        image.sprite = null;
        image.color = Color.white;

        yield return TweenScale(rect, rect.localScale, Vector3.one * WindowStartScale, WindowAnimationDuration);
        if (obj) Destroy(obj);
    }

    void ClearTauntWindows()
    {
        foreach (GameObject obj in activeTauntWindows.ToList())
            if (obj) StartCoroutine(AnimateTauntWindowOut(obj));

        activeTauntWindows.Clear();
    }

    IEnumerator TweenScale(RectTransform rect, Vector3 startScale, Vector3 endScale, float duration, Action? onComplete = null)
    {
        if (!rect) yield break;

        rect.localScale = startScale;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            if (!rect) yield break;
            elapsed += Time.deltaTime;
            float t = 1f - Mathf.Pow(1f - Mathf.Clamp01(elapsed / duration), 3f);
            rect.localScale = Vector3.Lerp(startScale, endScale, t);
            yield return null;
        }

        if (rect) rect.localScale = endScale;
        onComplete?.Invoke();
    }

    void TeleportRect(RectTransform r)
    {
        float w = r.rect.width, h = r.rect.height;
        r.anchoredPosition = new Vector2(
            UnityEngine.Random.Range(-Screen.width / 2f + w / 2f, Screen.width / 2f - w / 2f),
            UnityEngine.Random.Range(-Screen.height / 2f + h / 2f, Screen.height / 2f - h / 2f)
        );
    }

    void ScaleImageToScreen(Image image)
    {
        if (!image || !image.sprite) return;
        image.preserveAspect = true;

        float sw = image.sprite.rect.width, sh = image.sprite.rect.height;
        if (sw <= 0 || sh <= 0) return;

        float scale = Mathf.Min(Screen.width / sw, Screen.height / sh) * 0.95f;
        RectTransform rect = image.rectTransform;

        rect.localScale = warningOriginalScale;
        rect.sizeDelta = new Vector2(sw * scale, sh * scale);
        rect.anchoredPosition = Vector2.zero;
    }

    void RestoreWarningImageTransform()
    {
        if (!warningImage) return;
        RectTransform rect = warningImage.rectTransform;
        rect.localScale = warningOriginalScale;
        rect.sizeDelta = warningOriginalSizeDelta;
        rect.anchoredPosition = warningOriginalAnchoredPosition;
    }

    void SetupRansomCanvas()
    {
        GameObject obj = new GameObject("RansomCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        DontDestroyOnLoad(obj);

        ransomCanvas = obj.GetComponent<Canvas>();
        ransomCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        ransomCanvas.sortingOrder = 999;

        staticOverlay = CreateImage("StaticOverlay", ransomCanvas.transform, new Vector2(Screen.width, Screen.height));
        staticOverlay.sprite = staticFrames[0];
        staticOverlay.color = Color.white;

        vignetteOverlay = CreateImage("VignetteOverlay", ransomCanvas.transform, new Vector2(Screen.width, Screen.height));
        vignetteOverlay.sprite = vignetteFrames[0];
        vignetteOverlay.color = Color.white;

        warningImage = CreateImage("WarningImage", ransomCanvas.transform, new Vector2(400, 400));
        warningImage.sprite = idleSprite;
        warningImage.preserveAspect = true;

        thankYouImage = CreateImage("ThankYouImage", ransomCanvas.transform, new Vector2(600, 200));
        thankYouImage.sprite = thankYouTextSprite;
        thankYouImage.rectTransform.anchoredPosition = new Vector2(0, 100);

        okSignImage = CreateImage("OkSignImage", ransomCanvas.transform, new Vector2(200, 200));
        okSignImage.sprite = okSignSprite;
        okSignImage.rectTransform.anchoredPosition = new Vector2(0, -100);

        CreateRansomDemandWindow();
        CreateDownloadHUD();
        ResetVisualState();
    }

    void CreateRansomDemandWindow()
    {
        ransomDemandWindow = new GameObject("RansomDemandWindow", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        ransomDemandWindow.transform.SetParent(ransomCanvas.transform, false);

        RectTransform r = ransomDemandWindow.GetComponent<RectTransform>();
        r.sizeDelta = new Vector2(560, 400);

        ransomDemandBackground = ransomDemandWindow.GetComponent<Image>();
        ransomDemandBackground.color = new Color(0.97f, 0.17f, 0.11f);

        ransomDemandIdleIcon = CreateImage("RansomIdleIcon", ransomDemandWindow.transform, new Vector2(188, 188));
        ransomDemandIdleIcon.sprite = idleSprite;
        ransomDemandIdleIcon.rectTransform.anchoredPosition = new Vector2(-158, 94);

        ransomDemandTitle = CreateText("RansomDemandTitle", ransomDemandWindow.transform, LocalizationManager.Instance.GetLocalizedText("Ransom_Encrypted"), 36, Color.white);
        ransomDemandTitle.fontStyle = FontStyle.Bold;
        ransomDemandTitle.alignment = TextAnchor.MiddleCenter;
        ransomDemandTitle.rectTransform.sizeDelta = new Vector2(267, 154);
        ransomDemandTitle.rectTransform.anchoredPosition = new Vector2(86, 114);

        Image warningBoxBorder = CreateImage("WarningBoxBorder", ransomDemandWindow.transform, new Vector2(520, 109));
        warningBoxBorder.color = Color.white;
        warningBoxBorder.rectTransform.anchoredPosition = new Vector2(0, -20);

        Image warningBoxBg = CreateImage("WarningBoxBg", warningBoxBorder.transform, new Vector2(516, 105));
        warningBoxBg.color = Color.black;

        Text warningText = CreateText("WarningBoxText", warningBoxBg.transform, LocalizationManager.Instance.GetLocalizedText("Ransom_Warning"), 18, Color.white);
        warningText.fontStyle = FontStyle.Bold;
        warningText.alignment = TextAnchor.MiddleCenter;
        warningText.rectTransform.sizeDelta = new Vector2(500, 95);

        Image pointsBorder = CreateImage("PointsBorder", ransomDemandWindow.transform, new Vector2(189, 62));
        pointsBorder.color = new Color(1f, 0.81f, 0.15f);
        pointsBorder.rectTransform.anchoredPosition = new Vector2(-173, -117);

        Image pointsBg = CreateImage("PointsBg", pointsBorder.transform, new Vector2(185, 58));
        pointsBg.color = Color.black;

        ransomDemandAmount = CreateText("RansomDemandAmount", pointsBg.transform, "500", 38, new Color(1f, 0.81f, 0.15f));
        ransomDemandAmount.fontStyle = FontStyle.Bold;
        ransomDemandAmount.alignment = TextAnchor.MiddleLeft;
        ransomDemandAmount.rectTransform.sizeDelta = new Vector2(110, 50);
        ransomDemandAmount.rectTransform.anchoredPosition = new Vector2(-30, 0);

        Image goldIcon = CreateImage("GoldIcon", pointsBg.transform, new Vector2(50, 50));
        goldIcon.sprite = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == "YTP_Gold");
        goldIcon.rectTransform.anchoredPosition = new Vector2(60, 0);

        Image timerBorder = CreateImage("TimerBorder", ransomDemandWindow.transform, new Vector2(314, 62));
        timerBorder.color = Color.white;
        timerBorder.rectTransform.anchoredPosition = new Vector2(95, -117);

        Image timerBg = CreateImage("TimerBg", timerBorder.transform, new Vector2(310, 58));
        timerBg.color = Color.red;

        ransomDemandTimer = CreateText("RansomDemandTimer", timerBg.transform, LocalizationManager.Instance.GetLocalizedText("Ransom_Time"), 32, Color.black);
        ransomDemandTimer.fontStyle = FontStyle.Bold;
        ransomDemandTimer.alignment = TextAnchor.MiddleCenter;
        ransomDemandTimer.rectTransform.sizeDelta = new Vector2(300, 50);

        ransomDemandWindow.SetActive(false);
    }

    void ResetRansomDemandWindow()
    {
        if (!ransomDemandWindow) return;
        ransomDemandWindow.SetActive(false);

        RectTransform rect = ransomDemandWindow.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(560, 400);
        rect.anchoredPosition = Vector2.zero;
        rect.localScale = Vector3.one;

        if (ransomDemandTitle) ransomDemandTitle.gameObject.SetActive(true);
        if (ransomDemandAmount) ransomDemandAmount.transform.parent.parent.gameObject.SetActive(true);
        if (ransomDemandTimer) ransomDemandTimer.transform.parent.parent.gameObject.SetActive(true);

        Transform warningBox = ransomDemandWindow.transform.Find("WarningBoxBorder");
        if (warningBox) warningBox.gameObject.SetActive(true);

        if (thankYouImage)
        {
            thankYouImage.gameObject.SetActive(false);
            thankYouImage.transform.SetParent(ransomCanvas.transform, false);
            thankYouImage.rectTransform.anchoredPosition = new Vector2(0, 100);
            thankYouImage.rectTransform.sizeDelta = new Vector2(600, 200);
            thankYouImage.rectTransform.localScale = Vector3.one;
        }

        if (okSignImage)
        {
            okSignImage.gameObject.SetActive(false);
            okSignImage.transform.SetParent(ransomCanvas.transform, false);
            okSignImage.rectTransform.anchoredPosition = new Vector2(0, -100);
            okSignImage.rectTransform.sizeDelta = new Vector2(200, 200);
            okSignImage.rectTransform.localScale = Vector3.one;
        }

        if (ransomDemandIdleIcon) ransomDemandIdleIcon.gameObject.SetActive(true);
    }

    Image CreateImage(string name, Transform parent, Vector2 size = default)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        obj.transform.SetParent(parent, false);
        Image image = obj.GetComponent<Image>();
        image.rectTransform.sizeDelta = size;
        return image;
    }

    Text CreateText(string name, Transform parent, string text, int size, Color color)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        obj.transform.SetParent(parent, false);

        Text t = obj.GetComponent<Text>();
        t.font = Font.CreateDynamicFontFromOSFont("Consolas", size);
        t.fontSize = size;
        t.text = text;
        t.color = color;
        t.alignment = TextAnchor.MiddleCenter;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;

        RectTransform rect = t.rectTransform;
        rect.localScale = Vector3.one;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
        return t;
    }

    void SetFullRect(RectTransform r, float padding = 0)
    {
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.offsetMin = new Vector2(padding, padding);
        r.offsetMax = new Vector2(-padding, -padding);
    }

    void SetBarRect(RectTransform r, float height)
    {
        r.anchorMin = r.anchorMax = Vector2.zero;
        r.pivot = new Vector2(0, .5f);
        r.sizeDelta = new Vector2(0, height);
        r.anchoredPosition = Vector2.zero;
    }

    Texture2D GenerateVignetteTexture(int width, int height)
    {
        Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        Color[] colors = new Color[width * height];
        Vector2 center = new Vector2(width / 2f, height / 2f);
        float max = Vector2.Distance(Vector2.zero, center);

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), center) / max;
                float a = Mathf.Pow(d, 2.2f);
                colors[y * width + x] = UnityEngine.Random.value < (1 - d) * .35f
                    ? new Color(UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value, .7f)
                    : new Color(1, 0, 0, a);
            }

        tex.SetPixels(colors);
        tex.Apply();
        return tex;
    }

    Texture2D GenerateStaticTexture(int width, int height)
    {
        Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        Color[] colors = new Color[width * height];

        for (int i = 0; i < colors.Length; i++)
            colors[i] = new Color(UnityEngine.Random.value * .5f, 0, 0, 1);

        tex.SetPixels(colors);
        tex.Apply();
        return tex;
    }
}

[HarmonyPatch(typeof(CoreGameManager), "AddPoints", new Type[] { typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool) })]
internal static class CoreGameManager_AddPoints_Patch
{
    static bool Prefix(int points)
    {
        if (RansomPlugin.Instance?.IsUnderRansom == true)
        {
            RansomPlugin.Instance.AddRansomPoints(points);
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(ItemManager), "UseItem")]
internal static class ItemManager_UseItem_Patch
{
    static bool Prefix() => !(RansomPlugin.Instance?.IsUnderRansom ?? false);
}

[HarmonyPatch(typeof(ItemManager), "AddItem", new Type[] { typeof(ItemObject), typeof(Pickup) })]
internal static class ItemManager_AddItem_Pickup_Patch
{
    static bool Prefix() => !(RansomPlugin.Instance?.IsUnderRansom ?? false);
}

[HarmonyPatch(typeof(ItemManager), "AddItem", new Type[] { typeof(ItemObject) })]
internal static class ItemManager_AddItem_Direct_Patch
{
    static bool Prefix() => !(RansomPlugin.Instance?.IsUnderRansom ?? false);
}

[HarmonyPatch(typeof(HudManager), "Awake")]
internal static class HudManager_Awake_Patch
{
    static void Postfix() => RansomPlugin.Instance?.InitializeRansomCanvas();
}

[HarmonyPatch(typeof(HudManager), "SetItemSelect")]
internal static class HudManager_SetItemSelect_Patch
{
    static void Postfix(HudManager __instance, int value, string key)
    {
        if (RansomPlugin.Instance?.IsUnderRansom == true)
        {
            if (AccessTools.Field(typeof(HudManager), "itemTitle")?.GetValue(__instance) is TMP_Text title)
                title.text = LocalizationManager.Instance.GetLocalizedText("Ransom_EncryptedItem");
        }
    }
}

[HarmonyPatch(typeof(Elevator), "FinishLevel")]
internal static class Elevator_FinishLevel_Patch
{
    static void Prefix() => RansomPlugin.Instance?.ResetEverything();
}

[HarmonyPatch(typeof(CoreGameManager), "EndGame")]
internal static class CoreGameManager_EndGame_Patch
{
    static void Prefix() => RansomPlugin.Instance?.ResetEverything();
}

[HarmonyPatch(typeof(LocalizationManager), "LoadLocalizedText")]
internal static class LocalizationManager_LoadLocalizedText_Patch
{
    static void Postfix(LocalizationManager __instance)
    {
        if (AccessTools.Field(typeof(LocalizationManager), "localizedText")?.GetValue(__instance) is Dictionary<string, string> dict)
        {
            dict["Ransom_Downloading"] = "DOWNLOADING";
            dict["Ransom_Encrypted"] = "YOUR ITEMS HAVE BEEN ENCRYPTED";
            dict["Ransom_Warning"] = "IF YOU DO NOT PAY THIS RANSOM BEFORE THE TIMER ENDS, YOUR ITEMS WILL BE UNRECOVERABLE BY ANY MEANS.";
            dict["Ransom_Time"] = "TIME: {0:00}:{1:00}";
            dict["Ransom_EncryptedItem"] = "ENCRYPTED";
        }
    }
}
