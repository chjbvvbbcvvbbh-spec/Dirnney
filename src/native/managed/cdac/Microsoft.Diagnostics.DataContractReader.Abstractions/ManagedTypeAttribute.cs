// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Microsoft.Diagnostics.DataContractReader;

/// <summary>
/// Attaches the fully-qualified managed type name (in the system assembly) to a
/// <see cref="DataType"/> enum member. Consumed by <c>IMetadataLayoutSource</c> implementations
/// to look the type up via ECMA metadata.
/// </summary>
/// <remarks>
/// Nested types use <c>+</c> to separate the nested portion, e.g.
/// <c>System.Runtime.CompilerServices.ConditionalWeakTable`2+Entry</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class ManagedTypeAttribute : Attribute
{
    public ManagedTypeAttribute(string fullyQualifiedName)
    {
        FullyQualifiedName = fullyQualifiedName;
    }

    /// <summary>
    /// Fully-qualified managed type name, in the form <c>Namespace.TypeName</c>. Nested types
    /// use <c>+</c> to separate the nested portion.
    /// </summary>
    public string FullyQualifiedName { get; }
}
