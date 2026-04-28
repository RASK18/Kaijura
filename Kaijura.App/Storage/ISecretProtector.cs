namespace Kaijura.App.Storage;

public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string protectedValue);
}
