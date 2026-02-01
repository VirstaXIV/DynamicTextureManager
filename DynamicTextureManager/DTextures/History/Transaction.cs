namespace DynamicTextureManager.DTextures.History;

public interface ITransaction
{
    public ITransaction? Merge(ITransaction other);
    public void          Revert(IDTextureEditor editor, object data);
}