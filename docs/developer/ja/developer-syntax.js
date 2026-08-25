(() => {
  "use strict";

  const csharpKeywords = new Set([
    "abstract", "as", "base", "break", "case", "catch", "checked", "class",
    "const", "continue", "default", "delegate", "do", "else", "enum", "event",
    "explicit", "extern", "finally", "fixed", "for", "foreach", "goto", "if",
    "implicit", "in", "interface", "internal", "is", "lock", "namespace", "new",
    "operator", "out", "override", "params", "private", "protected", "public",
    "readonly", "record", "ref", "return", "sealed", "sizeof", "stackalloc",
    "static", "struct", "switch", "this", "throw", "try", "typeof", "unchecked",
    "unsafe", "using", "virtual", "volatile", "while", "and", "async", "await",
    "file", "get", "global", "init", "nameof", "not", "notnull", "or", "partial",
    "required", "scoped", "set", "unmanaged", "var", "when", "where", "with",
    "yield"
  ]);

  const csharpTypes = new Set([
    "bool", "byte", "char", "decimal", "double", "dynamic", "float", "int", "long",
    "nint", "nuint", "object", "sbyte", "short", "string", "uint", "ulong", "ushort",
    "void"
  ]);

  const csharpLiterals = new Set(["false", "null", "true"]);

  const qoraKeywords = new Set([
    "const", "Controlled", "else", "for", "function", "if", "import", "in", "move",
    "namespace", "new", "open", "operation", "repeat", "return", "until", "use",
    "var", "while"
  ]);

  const qoraTypes = new Set([
    "angle", "bit", "float", "int", "Qubit"
  ]);

  const qoraLiterals = new Set();
  const traceClasses = ["trace-create", "trace-update", "trace-read"];

  function appendToken(fragment, className, value) {
    if (className === null) {
      fragment.append(document.createTextNode(value));
      return;
    }

    const token = document.createElement("span");
    token.className = className;
    token.textContent = value;
    fragment.append(token);
  }

  function isIdentifierStart(character) {
    return character === "_"
      || character === "@"
      || character >= "A" && character <= "Z"
      || character >= "a" && character <= "z";
  }

  function isIdentifierPart(character) {
    return isIdentifierStart(character)
      || character >= "0" && character <= "9";
  }

  function nextNonWhitespace(text, start) {
    let index = start;
    while (index < text.length && /\s/.test(text[index]))
      index++;
    return text[index] ?? "";
  }

  function previousNonWhitespace(text, start) {
    let index = start - 1;
    while (index >= 0 && /\s/.test(text[index]))
      index--;
    return index >= 0 ? text[index] : "";
  }

  function isGenericCall(text, start) {
    if (text[start] !== "<")
      return false;

    let index = start;
    let depth = 0;
    for (; index < text.length; index++) {
      if (text.startsWith("//", index)
        || text.startsWith("/*", index)
        || text[index] === "\""
        || text[index] === "'") {
        return false;
      }

      if (text[index] === "<")
        depth++;
      else if (text[index] === ">") {
        depth--;
        if (depth === 0)
          return nextNonWhitespace(text, index + 1) === "(";
      }
    }

    return false;
  }

  function isPreprocessorStart(text, index) {
    const lineStart = text.lastIndexOf("\n", index - 1) + 1;
    return text.slice(lineStart, index).trim().length === 0;
  }

  function consumeQuoted(text, start, quote, verbatim) {
    let index = start + 1;
    while (index < text.length) {
      if (verbatim && text[index] === quote && text[index + 1] === quote) {
        index += 2;
        continue;
      }

      if (text[index] === quote)
        return index + 1;

      if (!verbatim && text[index] === "\\")
        index += 2;
      else
        index++;
    }

    return text.length;
  }

  function consumeString(text, start, language) {
    if (language === "csharp") {
      const rawMatch = text.slice(start).match(/^(\$*)("{3,})/);
      if (rawMatch !== null) {
        const delimiter = rawMatch[2];
        const contentStart = start + rawMatch[0].length;
        const closing = text.indexOf(delimiter, contentStart);
        return closing < 0 ? text.length : closing + delimiter.length;
      }

      const prefixMatch = text.slice(start).match(/^(?:\$@|@\$|\$|@)?"/);
      if (prefixMatch !== null) {
        const quoteIndex = start + prefixMatch[0].length - 1;
        const verbatim = prefixMatch[0].includes("@");
        return consumeQuoted(text, quoteIndex, "\"", verbatim);
      }
    }

    if (text[start] === "\"")
      return consumeQuoted(text, start, "\"", false);

    if (text[start] === "'")
      return consumeQuoted(text, start, "'", false);

    return start;
  }

  function tokenClassForIdentifier(
    identifier,
    language,
    previousCharacter,
    followingCharacter,
    genericCall) {
    const name = identifier.startsWith("@") ? identifier.slice(1) : identifier;
    const keywordSet = language === "csharp" ? csharpKeywords : qoraKeywords;
    const typeSet = language === "csharp" ? csharpTypes : qoraTypes;
    const literalSet = language === "csharp" ? csharpLiterals : qoraLiterals;
    const escapedIdentifier = language === "csharp" && identifier.startsWith("@");

    if (!escapedIdentifier && literalSet.has(name))
      return "syntax-literal";

    if (!escapedIdentifier && typeSet.has(name))
      return "syntax-type";

    if (!escapedIdentifier && keywordSet.has(name))
      return "syntax-keyword";

    if (followingCharacter === "(" || genericCall)
      return "syntax-callable";

    if (previousCharacter !== "."
      && name.length > 0
      && name[0] >= "A"
      && name[0] <= "Z")
      return "syntax-type";

    return null;
  }

  function highlightText(text, language) {
    const fragment = document.createDocumentFragment();
    let index = 0;

    while (index < text.length) {
      if (text.startsWith("//", index)) {
        const lineEnd = text.indexOf("\n", index + 2);
        const end = lineEnd < 0 ? text.length : lineEnd;
        appendToken(fragment, "syntax-comment", text.slice(index, end));
        index = end;
        continue;
      }

      if (text.startsWith("/*", index)) {
        const commentEnd = text.indexOf("*/", index + 2);
        const end = commentEnd < 0 ? text.length : commentEnd + 2;
        appendToken(fragment, "syntax-comment", text.slice(index, end));
        index = end;
        continue;
      }

      if (language === "csharp" && text[index] === "#" && isPreprocessorStart(text, index)) {
        const lineEnd = text.indexOf("\n", index + 1);
        const end = lineEnd < 0 ? text.length : lineEnd;
        appendToken(fragment, "syntax-preprocessor", text.slice(index, end));
        index = end;
        continue;
      }

      const stringEnd = consumeString(text, index, language);
      if (stringEnd > index) {
        appendToken(fragment, "syntax-string", text.slice(index, stringEnd));
        index = stringEnd;
        continue;
      }

      const numberMatch = text.slice(index).match(
        /^(?:0[xX][0-9a-fA-F_]+|0[bB][01_]+|(?:\d[\d_]*(?:\.\d[\d_]*)?|\.\d[\d_]*)(?:[eE][+-]?\d[\d_]*)?)(?:ul|lu|[uUlLfFdDmM])?/i);
      if (numberMatch !== null) {
        appendToken(fragment, "syntax-number", numberMatch[0]);
        index += numberMatch[0].length;
        continue;
      }

      if (isIdentifierStart(text[index])) {
        let end = index + 1;
        while (end < text.length && isIdentifierPart(text[end]))
          end++;

        const identifier = text.slice(index, end);
        const tokenClass = tokenClassForIdentifier(
          identifier,
          language,
          previousNonWhitespace(text, index),
          nextNonWhitespace(text, end),
          isGenericCall(text, end));
        appendToken(fragment, tokenClass, identifier);
        index = end;
        continue;
      }

      const operatorMatch = text.slice(index).match(
        /^(?:>>>=|>>>|>>=|<<=|\?\?=|=>|==|!=|<=|>=|&&|\|\||\+\+|--|\+=|-=|\*=|\/=|%=|&=|\|=|\^=|\?\?|\?\.|::|\.\.|->|[+\-*/%=&|^!~<>?:])/);
      if (operatorMatch !== null) {
        appendToken(fragment, "syntax-operator", operatorMatch[0]);
        index += operatorMatch[0].length;
        continue;
      }

      let end = index + 1;
      while (end < text.length
        && !text.startsWith("//", end)
        && !text.startsWith("/*", end)
        && !isIdentifierStart(text[end])
        && !/[\d"']/.test(text[end])
        && text[end] !== "$"
        && text[end] !== "#"
        && !/[+\-*/%=&|^!~<>?:]/.test(text[end])) {
        end++;
      }
      appendToken(fragment, null, text.slice(index, end));
      index = end;
    }

    return fragment;
  }

  function isTraceElement(element) {
    return traceClasses.some(className => element.classList.contains(className));
  }

  function highlightNode(node, language) {
    if (node.nodeType === Node.TEXT_NODE) {
      if (node.nodeValue.length > 0)
        node.replaceWith(highlightText(node.nodeValue, language));
      return;
    }

    if (node.nodeType !== Node.ELEMENT_NODE || isTraceElement(node))
      return;

    for (const child of [...node.childNodes])
      highlightNode(child, language);
  }

  function highlightCodeBlock(code) {
    if (code.dataset.syntaxHighlighted === "true")
      return;

    const language = code.classList.contains("language-csharp")
      ? "csharp"
      : code.classList.contains("language-qora")
        ? "qora"
        : null;

    if (language === null)
      return;

    for (const child of [...code.childNodes])
      highlightNode(child, language);

    code.dataset.syntaxHighlighted = "true";
  }

  function highlightAll() {
    const codeBlocks = document.querySelectorAll(
      "pre > code.language-csharp, pre > code.language-qora");
    for (const code of codeBlocks)
      highlightCodeBlock(code);
  }

  if (document.readyState === "loading")
    document.addEventListener("DOMContentLoaded", highlightAll, { once: true });
  else
    highlightAll();
})();
