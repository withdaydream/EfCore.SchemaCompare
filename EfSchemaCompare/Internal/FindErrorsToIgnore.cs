// Copyright (c) 2024 Jon P Smith, GitHub: JonPSmith, web: http://www.thereformedprogrammer.net/
// Licensed under MIT license. See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Test")]

namespace EfSchemaCompare.Internal;

/// <summary>
/// This class handles the ignoring of errors that the users wants to be ignored.
/// </summary>
internal class FindErrorsToIgnore
{
    internal static CompareLog DecodeCompareTextToCompareLog(string errorString)
    {
        errorString = errorString.Trim();

        //These parts of the errors are the same in all the error pattern
        var state = GetEnumType<CompareState>(errorString, null, ": ");
        var attribute = GetAttribute(errorString);

        //string items. Some errors don't have Expected or Found, in which case it has null 
        var name = GetStringInError(errorString, "'", "'");
        //"Expected" can either have a "Found" after it, or not,
        //so we get everything to the end and take off the "Found" part if it's there.
        //It looks for the whole ", found = " as the expected text can contain ", ", e.g. "Columns(A, B)"
        var expected = GetStringInError(errorString, "Expected = ", null);
        if (expected != null)
        {
            var possibleFoundStart = expected.IndexOf(", found = ", StringComparison.Ordinal);
            if (possibleFoundStart> 0)
                expected = expected.Substring(0, possibleFoundStart);
        }
        var found = GetStringInError(errorString, " found = ", null, true);

        //The type part has two patterns
        //1. "OK: DbContext ...", where "DbContext" is the type
        //2. "NOT IN DATABASE: BookDetail->ForeignKey ...", where "ForeignKey" is the type
        //The "->" is only looked for before the name, as the expected/found text can contain "->", e.g. a trigger's json operator
        var nameStart = errorString.IndexOf('\'');
        var beforeString = (nameStart < 0 ? errorString : errorString.Substring(0, nameStart)).Contains("->") ? "->" : ": ";
        var type = GetEnumType<CompareType>(errorString, beforeString, " ");

        //These are either different in the various errors strings, or aren't in some error pattens

        return new CompareLog(type, state, name, attribute, expected, found);
    }

    /// <summary>
    /// This returns a section of the errorString, defined by unique before and end strings
    /// e.g. an errorString of "EXTRA IN DATABASE: Table 'Customers'" you could extract the
    /// "Table" by setting beforeString to ": " and setting afterString to "\'".
    /// NOTE: If the beforeString and afterString aren't found it returns an empty string
    /// </summary>
    /// <param name="errorString">A string containing an error created during  </param>
    /// <param name="beforeString">NOTE: If the beforeString is null/empty it will start at the first letter</param>
    /// <param name="afterString">NOTE: If the beforeString is null/empty it will end it will go to the end </param>
    /// <param name="ignoreCase">Set this to find 'found = ', which is 'Found = ' in an ExtraInDatabase error
    /// and 'found = ' when it follows an 'Expected = '</param>
    /// <returns>Returns the string between the two start/end strings.
    /// NOTE: If either the beforeString or afterString aren't found it will return null, i.e. not found</returns>
    private static string GetStringInError(string errorString, string beforeString, string afterString, bool ignoreCase = false)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var typeStart = 0;
        if (!String.IsNullOrEmpty(beforeString))
        {
            var beforeIndex = errorString.IndexOf(beforeString, comparison);
            if (beforeIndex == -1)
                return null; //Could not find the beforeString
            typeStart = beforeIndex + beforeString.Length;
        }

        var typeEnd = errorString.Length;
        if (!String.IsNullOrEmpty(afterString))
        {
            typeEnd = errorString.IndexOf(afterString, typeStart, comparison);
            if (typeEnd == -1)
                return null; //Could not find the afterString
        }

        var result = errorString.Substring(typeStart, typeEnd - typeStart);
        return result.Length == 0 || result == "<null>" ? null : result;
    }

    /// <summary>
    /// The attribute, if there is one, directly follows the quoted name, e.g. "DIFFERENT: Column 'Id', column type. Expected = ..."
    /// It is found from the name's closing quote, as the expected/found text can contain ", " and ". ", e.g. a trigger definition
    /// </summary>
    /// <param name="errorString">The error message</param>
    /// <returns>The attribute, or NotSet if there isn't one</returns>
    private static CompareAttributes GetAttribute(string errorString)
    {
        var nameStart = errorString.IndexOf('\'');
        var nameEnd = nameStart < 0 ? -1 : errorString.IndexOf('\'', nameStart + 1);
        if (nameEnd < 0 || string.CompareOrdinal(errorString, nameEnd + 1, ", ", 0, 2) != 0)
            return CompareAttributes.NotSet;

        var attributeStart = nameEnd + 3;
        var attributeEnd = errorString.IndexOf(". ", attributeStart, StringComparison.Ordinal);
        var attributeText = attributeEnd < 0
            ? errorString.Substring(attributeStart)
            : errorString.Substring(attributeStart, attributeEnd - attributeStart);
        return (CompareAttributes)Enum.Parse(typeof(CompareAttributes), attributeText.Replace(" ", ""), true);
    }

    /// <summary>
    /// This takes a string holding the name of the Enum type and returns the correct Type/Enum.
    /// If there is no Enum Name, then it returns the first Enum
    /// See <see cref="GetStringInError"/> summary for the details of how it got the string to be turned into an Enum
    /// </summary>
    /// <typeparam name="T">Is <see cref="CompareState"/>, <see cref="CompareType"/>, or <see cref="CompareAttributes"/></typeparam>
    /// <param name="errorString">The error message</param>
    /// <param name="beforeString">A unique string that is before the error message</param>
    /// <param name="afterString">A unique string that comes after the error message</param>
    /// <returns>The correct Type/Enum entry</returns>
    private static T GetEnumType<T>(string errorString, string beforeString, string afterString) where T : Enum
    {
        var foundSection = GetStringInError(errorString, beforeString, afterString);
        if (foundSection == null)
            return Enum.GetValues(typeof(T)).Cast<T>().First();
        return (T)Enum.Parse(typeof(T), foundSection.Replace(" ", ""), true);
    }


}