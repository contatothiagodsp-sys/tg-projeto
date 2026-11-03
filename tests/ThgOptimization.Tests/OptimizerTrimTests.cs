using System.Reflection;

namespace ThgOptimization.Tests;

/// <summary>Trava de regressão: tirar páginas da RAM à força gera page faults e engasgos no jogo. Essas chamadas não podem voltar.</summary>
public class OptimizerTrimTests
{
    private static bool Imports(Type type, string function) =>
        type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Any(m => m.Name == function && m.Attributes.HasFlag(MethodAttributes.PinvokeImpl));

    [Fact]
    public void Limpeza_nao_mexe_na_memoria_dos_outros_programas() =>
        Assert.False(Imports(typeof(Optimizer), "EmptyWorkingSet"));

    [Fact]
    public void Trim_nao_tira_paginas_do_proprio_app() =>
        Assert.False(Imports(typeof(MemoryRelease), "SetProcessWorkingSetSize"));
}
