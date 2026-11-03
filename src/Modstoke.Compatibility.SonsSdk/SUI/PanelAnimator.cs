using UnityEngine;

namespace SUI;

public class PanelPosAnimator
{
    public Vector3? StartPos;
    public Vector3 Distance;
    public float Time;

    public PanelPosAnimator(Vector3 distance, float time = 0.5f)
    {
        Distance = distance;
        Time = time;
    }

    public PanelPosAnimator(float? x = null, float? y = null, float? z = null, float time = 0.5f)
    {
        Distance = new(x ?? 0, y ?? 0, z ?? 0);
        Time = time;
    }

    public void AnimIn(SContainerOptions container)
    {
        container.Active(true);
    }
    
    public void AnimOut(SContainerOptions container)
    {
        container.Active(false);
    }
    
    public static PanelPosAnimator FromBottom = new(new Vector3(0, 300, 0));
    public static PanelPosAnimator FromTop = new(new Vector3(0, -300, 0));
    public static PanelPosAnimator FromLeft = new(new Vector3(300, 0, 0));
    public static PanelPosAnimator FromRight = new(new Vector3(-300, 0, 0));
}

public class PanelSizeAnimator
{
    public Vector2? StartSize;
    public Vector2 Size;
    public float Time;

    public PanelSizeAnimator(Vector2 size, float time = 0.5f)
    {
        Size = size;
        Time = time;
    }

    public void AnimIn(SContainerOptions container)
    {
        container.Active(true);
        StartSize ??= container.RectTransform.sizeDelta;
        container.RectTransform.sizeDelta = StartSize.Value - Size;
    }
    
    public void AnimOut(SContainerOptions container)
    {
        container.Active(false);
    }
}
