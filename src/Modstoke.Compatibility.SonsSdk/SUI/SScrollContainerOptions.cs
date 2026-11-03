using SonsSdk;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SUI;

public class SScrollContainerOptions : SContainerOptions
{
    public SContainerOptions ContainerObject;

    public SScrollContainerOptions(GameObject root) : base(root)
    {
        ContainerObject = new SContainerOptions(root.transform.Find("Viewport/Content").gameObject);
        Object.DestroyImmediate(root.GetComponent<Image>());
        root.SetActive(true);
    }
    
    public SScrollContainerOptions ContainerPadding(int padding)
    {
        ContainerObject.Padding(padding);
        return this;
    }
    
    public SScrollContainerOptions ContainerPadding(int left, int right, int top, int bottom)
    {
        ContainerObject.Padding(left, top, right, bottom);
        return this;
    }

    public override SScrollContainerOptions Add(SUiElement element)
    {
        element.SetParent(ContainerObject);
        AddChildReferences(element);

        return this;
    }
}
