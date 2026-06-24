using System;
using System.Collections;

public interface ILLMClient
{
    IEnumerator SendPrompt(string prompt, Action<string> onResponse);
}
