#region

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

#endregion

namespace NebulaWorld;

public static class InGamePopup
{
    private const float minWindowWidth = 520f;

    private static UIMessageBox displayedMessage;

    public static void FadeOut()
    {
        if (displayedMessage == null)
        {
            return;
        }
        displayedMessage.FadeOut();
        displayedMessage = null;
    }

    public static void UpdateMessage(in string title, string message)
    {
        if (displayedMessage == null || displayedMessage.m_TitleText.text != title)
        {
            return;
        }
        displayedMessage.m_MessageText.horizontalOverflow = HorizontalWrapMode.Overflow;
        displayedMessage.m_MessageText.verticalOverflow = VerticalWrapMode.Overflow;
        displayedMessage.m_MessageText.text = message;
    }

    // Input
    public static void AskInput(string title, string message, InputField.ContentType inputType, string inputText,
        Action<string> onConfirm, Action onCancel)
    {
        displayedMessage = UIMessageBox.Show(title, message, "取消".Translate(), "确定".Translate(),
            UIMessageBox.QUESTION, () => { onCancel?.Invoke(); }, () => { onConfirm?.Invoke(GetInputField()); });
        CreateInputField(inputType, inputText);
    }

    // Info
    public static void ShowInfo(string title, string message, string btn1, Action resp1 = null)
    {
        Show(UIMessageBox.INFO, title, message, btn1, resp1);
    }

    public static void ShowInfo(string title, string message, string btn1, string btn2, Action resp1, Action resp2)
    {
        Show(UIMessageBox.INFO, title, message, btn1, btn2, resp1, resp2);
    }

    public static void ShowInfo(string title, string message, string btn1, string btn2, string btn3, Action resp1, Action resp2,
        Action resp3)
    {
        Show(UIMessageBox.INFO, title, message, btn1, btn2, btn3, resp1, resp2, resp3);
    }

    // Warning
    public static void ShowWarning(string title, string message, string btn1, Action resp1 = null)
    {
        Show(UIMessageBox.WARNING, title, message, btn1, resp1);
    }

    public static void ShowWarning(string title, string message, string btn1, string btn2, Action resp1, Action resp2)
    {
        Show(UIMessageBox.WARNING, title, message, btn1, btn2, resp1, resp2);
    }

    public static void ShowWarning(string title, string message, string btn1, string btn2, string btn3, Action resp1,
        Action resp2, Action resp3)
    {
        Show(UIMessageBox.WARNING, title, message, btn1, btn2, btn3, resp1, resp2, resp3);
    }

    // Question
    public static void ShowQuestion(string title, string message, string btn1, Action resp1 = null)
    {
        Show(UIMessageBox.QUESTION, title, message, btn1, resp1);
    }

    public static void ShowQuestion(string title, string message, string btn1, string btn2, Action resp1, Action resp2)
    {
        Show(UIMessageBox.QUESTION, title, message, btn1, btn2, resp1, resp2);
    }

    public static void ShowQuestion(string title, string message, string btn1, string btn2, string btn3, Action resp1,
        Action resp2, Action resp3)
    {
        Show(UIMessageBox.QUESTION, title, message, btn1, btn2, btn3, resp1, resp2, resp3);
    }

    // Error
    public static void ShowError(string title, string message, string btn1, Action resp1 = null)
    {
        Show(UIMessageBox.ERROR, title, message, btn1, resp1);
    }

    public static void ShowError(string title, string message, string btn1, string btn2, Action resp1, Action resp2)
    {
        Show(UIMessageBox.ERROR, title, message, btn1, btn2, resp1, resp2);
    }

    public static void ShowError(string title, string message, string btn1, string btn2, string btn3, Action resp1,
        Action resp2, Action resp3)
    {
        Show(UIMessageBox.ERROR, title, message, btn1, btn2, btn3, resp1, resp2, resp3);
    }

    // Base
    private static void Show(int type, string title, string message, string btn1, Action resp1 = null)
    {
        displayedMessage = UIMessageBox.Show(title, message, btn1, type, () => { resp1?.Invoke(); });
    }

    private static void Show(int type, string title, string message, string btn1, string btn2, Action resp1, Action resp2)
    {
        displayedMessage = UIMessageBox.Show(title, message, btn1, btn2, type, () => { resp1?.Invoke(); },
            () => { resp2?.Invoke(); });
    }

    private static void Show(int type, string title, string message, string btn1, string btn2, string btn3, Action resp1,
        Action resp2, Action resp3)
    {
        displayedMessage = UIMessageBox.Show(title, message, btn1, btn2, btn3, type, () => { resp1?.Invoke(); },
            () => { resp2?.Invoke(); }, () => { resp3?.Invoke(); });
    }

    private static void CreateInputField(InputField.ContentType contentType, string text)
    {
        var client = displayedMessage.transform.Find("Window/Body/Client") as RectTransform;
        var inputObject = Object.Instantiate(PopupInputSource(), client);
        inputObject.name = "InputField";
        inputObject.SetActive(true);
        var inputField = inputObject.GetComponent<InputField>();
        inputField.onEndEdit.RemoveAllListeners();
        inputField.onValueChanged.RemoveAllListeners();
        inputField.contentType = contentType;
        inputField.text = text;
        if (inputField.placeholder is Text placeholder) placeholder.text = string.Empty;
        LayoutInputRow(inputField, client);
        inputField.ActivateInputField();
    }

    /// <summary>The same input widget the server editor uses, so every mod prompt looks alike;
    /// the save-game name field is the fallback when the galaxy select is unavailable.</summary>
    private static GameObject PopupInputSource()
    {
        var galaxySelect = UIRoot.instance != null ? UIRoot.instance.galaxySelect : null;
        var seed = galaxySelect != null
            ? galaxySelect.transform.Find("setting-group/stretch-transform/galaxy-seed")
            : null;
        var seedInput = seed != null ? seed.GetComponentInChildren<InputField>(true) : null;
        return seedInput != null ? seedInput.gameObject : UIRoot.instance.saveGameWindow.nameInput.gameObject;
    }

    /// <summary>
    ///     The message box prefab is sized for long essays, so a short password prompt
    ///     floats in dead space. Measure the real content, stretch the window wide enough
    ///     for a full input row, and park that row on its own line under the message.
    ///     The buttons are anchored to the window's bottom edge, so they ride along on
    ///     their own. Each popup is a fresh instance destroyed on close, so moving its
    ///     pieces cannot leak into other message boxes.
    /// </summary>
    private static void LayoutInputRow(InputField input, RectTransform client)
    {
        const float sideMargin = 28f;
        const float rowGap = 14f;
        const float minWindowHeight = 230f;
        const float edgeKeep = 36f;

        // The message box lays its buttons out on the next canvas update; measure them after
        // a forced pass or the bounds come back pre-layout.
        Canvas.ForceUpdateCanvases();
        var buttons = new List<RectTransform>();
        var buttonHeight = 0f;
        foreach (var buttonText in new[] { displayedMessage.m_Button1, displayedMessage.m_Button2, displayedMessage.m_Button3 })
        {
            if (buttonText == null) continue;
            var button = buttonText.transform.parent as RectTransform;
            if (button == null || !button.gameObject.activeInHierarchy) continue;
            buttons.Add(button);
            buttonHeight = Mathf.Max(buttonHeight,
                RectTransformUtility.CalculateRelativeRectTransformBounds(client, button).size.y);
        }
        var inputHeight = Mathf.Max(34f, buttonHeight);
        var window = displayedMessage.m_WindowTrans;

        // Width first: hugging the content may re-wrap the message, which would change the
        // vertical measurements below.
        window.sizeDelta = new Vector2(
            Mathf.Max(Mathf.Min(window.sizeDelta.x, minWindowWidth), CompactWidth(client, window, edgeKeep)),
            window.sizeDelta.y);
        Canvas.ForceUpdateCanvases();

        float contentBottom = ContentBottom(client);
        float buttonsTop = ButtonsTop(client, buttons);

        // The input row takes a full line of its own under the message. The prefab's gap
        // between the message and the buttons is smaller than that line, so the window
        // grows downward and the title edge stays where it is.
        var needed = inputHeight + rowGap * 2f;
        var delta = Mathf.Max(0f, needed - (contentBottom - buttonsTop));
        window.sizeDelta = new Vector2(window.sizeDelta.x, Mathf.Max(minWindowHeight, window.sizeDelta.y + delta));
        window.anchoredPosition -= new Vector2(0f, delta / 2f);
        Canvas.ForceUpdateCanvases();

        // Everything may have shifted with the resize; measure the freed row again.
        contentBottom = ContentBottom(client);
        buttonsTop = ButtonsTop(client, buttons);
        var anchorOffset = new Vector2((0.5f - client.pivot.x) * client.rect.width,
            (0.5f - client.pivot.y) * client.rect.height);
        var rect = (RectTransform)input.transform;
        // The field sits on its own row under the message, spanning the dialog instead of
        // sharing the message line. Its top edge starts one gap below the message.
        var width = Mathf.Max(240f, client.rect.width - sideMargin * 2f);
        var centerY = contentBottom - rowGap - inputHeight / 2f;

        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(width, inputHeight);
        rect.anchoredPosition = new Vector2(-anchorOffset.x, centerY - anchorOffset.y);
    }

    /// <summary>The prefab hugs the message line, which is too narrow once the input sits on
    /// its own row underneath. Stretch toward the wider of the message and the minimum that
    /// fits a full-width field, and never past the prefab's own width.</summary>
    private static float CompactWidth(RectTransform client, RectTransform window, float edgeKeep)
    {
        var text = displayedMessage.m_MessageText;
        var content = text != null ? text.preferredWidth : 0f;
        var icon = displayedMessage.m_IconImage;
        if (icon != null && icon.gameObject.activeInHierarchy)
        {
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(client, icon.rectTransform);
            content += bounds.size.x + 16f;
        }
        var chrome = window.rect.width - client.rect.width;
        var target = Mathf.Max(content + edgeKeep * 2f + chrome, minWindowWidth);
        return Mathf.Clamp(target, minWindowWidth, Mathf.Max(window.sizeDelta.x, minWindowWidth));
    }

    private static float ContentBottom(RectTransform client)
    {
        var bottom = float.MaxValue;
        if (displayedMessage.m_MessageText != null)
        {
            bottom = Mathf.Min(bottom, RectTransformUtility
                .CalculateRelativeRectTransformBounds(client, displayedMessage.m_MessageText.rectTransform).min.y);
        }
        if (displayedMessage.m_IconImage != null && displayedMessage.m_IconImage.gameObject.activeInHierarchy)
        {
            bottom = Mathf.Min(bottom, RectTransformUtility
                .CalculateRelativeRectTransformBounds(client, displayedMessage.m_IconImage.rectTransform).min.y);
        }
        return bottom == float.MaxValue ? client.rect.yMax : bottom;
    }

    private static float ButtonsTop(RectTransform client, List<RectTransform> buttons)
    {
        var top = float.MinValue;
        foreach (var button in buttons)
        {
            top = Mathf.Max(top, RectTransformUtility.CalculateRelativeRectTransformBounds(client, button).max.y);
        }
        return top == float.MinValue ? client.rect.yMin : top;
    }

    private static string GetInputField()
    {
        return displayedMessage.transform.Find("Window/Body/Client/InputField").GetComponent<InputField>().text;
    }
}
