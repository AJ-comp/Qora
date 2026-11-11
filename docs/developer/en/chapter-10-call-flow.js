(() => {
  "use strict";

  function copyWithLegacyApi(text) {
    const textarea = document.createElement("textarea");
    textarea.value = text;
    textarea.setAttribute("readonly", "");
    textarea.style.position = "fixed";
    textarea.style.inset = "0 auto auto -9999px";
    document.body.append(textarea);
    try {
      textarea.select();
      if (!document.execCommand("copy"))
        throw new Error("copy command failed");
    }
    finally {
      textarea.remove();
    }
  }

  async function writeToClipboard(text) {
    if (globalThis.navigator?.clipboard?.writeText) {
      try {
        await globalThis.navigator.clipboard.writeText(text);
        return;
      }
      catch {
        // If clipboard permission is unavailable, fall back to copying the selected textarea.
      }
    }

    copyWithLegacyApi(text);
  }

  function addCodeCopyButtons() {
    const codeBlocks = document.querySelectorAll(
      ".function-trace pre > code.language-csharp, "
      + ".function-trace pre > code.language-qora");

    for (const code of codeBlocks) {
      const pre = code.parentElement;
      if (pre === null
        || pre.parentElement?.classList.contains("code-copy-shell")) {
        continue;
      }

      const copyText = code.textContent ?? "";
      const shell = document.createElement("div");
      shell.className = "code-copy-shell";

      const button = document.createElement("button");
      button.type = "button";
      button.className = "code-copy-button";
      button.textContent = "Copy";
      button.setAttribute("aria-label", "Copy code");
      button.setAttribute("aria-live", "polite");

      let resetTimer = null;
      button.addEventListener("click", async () => {
        if (resetTimer !== null)
          window.clearTimeout(resetTimer);

        button.disabled = true;
        try {
          await writeToClipboard(copyText);
          button.textContent = "Copied";
          button.dataset.copyState = "copied";
          button.setAttribute("aria-label", "Code copied");
        }
        catch {
          button.textContent = "Copy failed";
          button.dataset.copyState = "failed";
          button.setAttribute("aria-label", "Could not copy code");
        }
        finally {
          button.disabled = false;
          button.focus({ preventScroll: true });
        }

        resetTimer = window.setTimeout(() => {
          button.textContent = "Copy";
          button.dataset.copyState = "";
          button.setAttribute("aria-label", "Copy code");
          resetTimer = null;
        }, 1800);
      });

      pre.before(shell);
      shell.append(button, pre);
    }
  }

  function positionAnnotatedCodeMap(map) {
    const source = map.querySelector(".annotated-code-map__source");
    const notes = map.querySelector(".annotated-code-map__notes");
    const arrows = map.querySelector(".annotated-code-map__arrows");
    const noteElements = [...map.querySelectorAll(".annotated-code-note")];
    if (source === null || notes === null || arrows === null)
      return;

    for (const arrow of arrows.querySelectorAll(".annotated-code-arrow"))
      arrow.remove();

    // Follow the actual card layout, including narrow content containers.
    const notesAreStacked = noteElements.length === 0
      || getComputedStyle(noteElements[0]).position === "static";
    if (notesAreStacked) {
      notes.style.height = "";
      map.style.minHeight = "";
      for (const note of noteElements)
        note.style.top = "";
      return;
    }

    const mapRect = map.getBoundingClientRect();
    const sourceRect = source.getBoundingClientRect();
    const noteLift = 132;
    let previousBottom = -14;

    for (const note of noteElements) {
      const targetId = note.dataset.codeTarget;
      const target = targetId ? document.getElementById(targetId) : null;
      if (target === null)
        continue;

      const targetRect = target.getBoundingClientRect();
      const desiredTop = targetRect.top - mapRect.top
        + (targetRect.height - note.offsetHeight) / 2;
      const top = Math.max(0, desiredTop - noteLift, previousBottom + 14);
      note.style.top = `${top}px`;
      previousBottom = top + note.offsetHeight;
    }

    const requiredHeight = Math.max(sourceRect.height, previousBottom);
    notes.style.height = `${requiredHeight}px`;
    map.style.minHeight = `${requiredHeight}px`;

    const updatedMapRect = map.getBoundingClientRect();
    const svgNamespace = "http://www.w3.org/2000/svg";
    const markerId = arrows.dataset.markerId;

    for (const note of noteElements) {
      const targetId = note.dataset.codeTarget;
      const target = targetId ? document.getElementById(targetId) : null;
      if (target === null)
        continue;

      const targetRect = target.getBoundingClientRect();
      const noteRect = note.getBoundingClientRect();
      const startX = targetRect.right - updatedMapRect.left;
      const startY = targetRect.top - updatedMapRect.top + targetRect.height / 2;
      const endX = noteRect.left - updatedMapRect.left;
      const endY = noteRect.top - updatedMapRect.top + noteRect.height / 2;
      const horizontalDistance = Math.max(36, (endX - startX) * .42);

      const path = document.createElementNS(svgNamespace, "path");
      path.classList.add("annotated-code-arrow");
      path.setAttribute(
        "d",
        `M${startX} ${startY} C${startX + horizontalDistance} ${startY} `
        + `${endX - horizontalDistance} ${endY} ${endX} ${endY}`);
      path.setAttribute(
        "stroke",
        getComputedStyle(note).getPropertyValue("--stage-stroke").trim());
      if (markerId)
        path.setAttribute("marker-end", `url(#${markerId})`);
      arrows.append(path);
    }
  }

  let annotatedCodeLayoutFrame = null;
  function scheduleAnnotatedCodeLayout() {
    if (annotatedCodeLayoutFrame !== null)
      window.cancelAnimationFrame(annotatedCodeLayoutFrame);

    annotatedCodeLayoutFrame = window.requestAnimationFrame(() => {
      annotatedCodeLayoutFrame = null;
      for (const map of document.querySelectorAll(".annotated-code-map"))
        positionAnnotatedCodeMap(map);
    });
  }

  function initializeAnnotatedCodeMaps() {
    scheduleAnnotatedCodeLayout();
    window.addEventListener("resize", scheduleAnnotatedCodeLayout);

    if ("ResizeObserver" in window) {
      const observer = new ResizeObserver(scheduleAnnotatedCodeLayout);
      for (const map of document.querySelectorAll(".annotated-code-map"))
        observer.observe(map);
    }
  }

  function initializeCallerReturnButtons() {
    const callerStackStorageKey = `qoraDetailCallerStack:v2:${window.location.pathname}`;
    const detailFlowLinkSelector = 'a.code-detail-region[href^="#"]';

    function parseUrl(href) {
      try {
        return new URL(href, window.location.href);
      } catch {
        return null;
      }
    }

    function selectedHash(url) {
      return url.hash || "#lower-hir-artifact";
    }

    function isSameDocumentPath(left, right) {
      return left.origin === right.origin
        && left.pathname === right.pathname;
    }

    function isSameSelectedFunction(leftHref, rightHref) {
      const left = parseUrl(leftHref);
      const right = parseUrl(rightHref);
      return left !== null
        && right !== null
        && isSameDocumentPath(left, right)
        && selectedHash(left) === selectedHash(right);
    }

    function readCallerStack() {
      try {
        const storedValue = window.sessionStorage.getItem(callerStackStorageKey);
        if (storedValue === null)
          return [];

        const callerStack = JSON.parse(storedValue);
        return Array.isArray(callerStack)
          ? callerStack.filter(callerHref => typeof callerHref === "string")
          : [];
      } catch {
        return [];
      }
    }

    function writeCallerStack(callerStack) {
      try {
        if (callerStack.length === 0)
          window.sessionStorage.removeItem(callerStackStorageKey);
        else
          window.sessionStorage.setItem(callerStackStorageKey, JSON.stringify(callerStack));
      } catch {
        // Keep ordinary link navigation available when storage is unavailable.
      }
    }

    function callerHrefForLink(link) {
      const callerArticle = link.closest(".function-trace");
      const targetUrl = parseUrl(link.href);
      if (callerArticle === null
        || callerArticle.id.length === 0
        || targetUrl === null) {
        return null;
      }

      const callerUrl = new URL(window.location.href);
      callerUrl.hash = callerArticle.id;
      return isSameSelectedFunction(callerUrl.href, targetUrl.href)
        ? null
        : callerUrl.href;
    }

    function inferredCallerHref() {
      const currentUrl = new URL(window.location.href);
      for (const link of document.querySelectorAll(detailFlowLinkSelector)) {
        const targetUrl = parseUrl(link.href);
        if (targetUrl === null
          || !isSameDocumentPath(targetUrl, currentUrl)
          || selectedHash(targetUrl) !== selectedHash(currentUrl)) {
          continue;
        }

        return callerHrefForLink(link);
      }

      return null;
    }

    const currentUrl = new URL(window.location.href);
    const referrerUrl = document.referrer.length > 0
      ? parseUrl(document.referrer)
      : null;
    const navigationType = window.performance
      .getEntriesByType("navigation")[0]?.type;

    if (referrerUrl !== null
      && !isSameDocumentPath(referrerUrl, currentUrl)) {
      writeCallerStack([referrerUrl.href]);
    } else if (navigationType !== "reload") {
      writeCallerStack([]);
    }

    for (const link of document.querySelectorAll(detailFlowLinkSelector)) {
      link.addEventListener("click", event => {
        if (event.defaultPrevented
          || event.button !== 0
          || event.ctrlKey
          || event.metaKey
          || event.shiftKey
          || event.altKey) {
          return;
        }

        const callerHref = callerHrefForLink(link);
        if (callerHref === null)
          return;

        const callerStack = readCallerStack();
        const previousCallerHref = callerStack.at(-1);
        if (previousCallerHref === undefined
          || !isSameSelectedFunction(previousCallerHref, callerHref)) {
          callerStack.push(callerHref);
        }
        writeCallerStack(callerStack);
      });
    }

    const buttons = document.querySelectorAll("[data-return-to-caller]");
    for (const button of buttons) {
      button.addEventListener("click", () => {
        const callerStack = readCallerStack();
        let callerHref = null;
        while (callerStack.length > 0) {
          const candidateHref = callerStack.pop();
          if (candidateHref
            && !isSameSelectedFunction(candidateHref, window.location.href)) {
            callerHref = candidateHref;
            break;
          }
        }
        writeCallerStack(callerStack);

        if (callerHref !== null) {
          window.location.href = callerHref;
          return;
        }

        const currentButtonUrl = new URL(window.location.href);
        const currentReferrerUrl = document.referrer.length > 0
          ? parseUrl(document.referrer)
          : null;
        if (currentReferrerUrl !== null
          && !isSameDocumentPath(currentReferrerUrl, currentButtonUrl)) {
          window.location.href = currentReferrerUrl.href;
          return;
        }

        const inferredHref = inferredCallerHref();
        if (inferredHref !== null
          && !isSameSelectedFunction(inferredHref, window.location.href)) {
          window.location.href = inferredHref;
          return;
        }

        const fallbackHref = button.dataset.fallbackHref;
        if (fallbackHref)
          window.location.href = fallbackHref;
      });
    }
  }

  function moveToSelectedFunction() {
    const functionId = decodeURIComponent(window.location.hash.slice(1));
    if (functionId.length === 0)
      return;

    const selectedFunction = document.getElementById(functionId);
    if (selectedFunction === null
      || !selectedFunction.classList.contains("function-trace")) {
      return;
    }

    function positionSelectedFunction() {
      const documentRoot = document.documentElement;
      const previousScrollBehavior = documentRoot.style.scrollBehavior;
      documentRoot.style.scrollBehavior = "auto";
      const topReturnNavigation = document.querySelector(".caller-return-nav");
      (topReturnNavigation ?? selectedFunction).scrollIntoView({ block: "start", behavior: "auto" });
      documentRoot.style.scrollBehavior = previousScrollBehavior;

      const heading = selectedFunction.querySelector("h2");
      if (heading !== null) {
        heading.tabIndex = -1;
        heading.focus({ preventScroll: true });
      }
    }

    positionSelectedFunction();
    window.requestAnimationFrame(positionSelectedFunction);
    scheduleAnnotatedCodeLayout();
  }

  window.addEventListener("hashchange", moveToSelectedFunction);

  addCodeCopyButtons();
  initializeCallerReturnButtons();

  if (document.readyState === "loading")
    window.addEventListener("DOMContentLoaded", initializeAnnotatedCodeMaps, { once: true });
  else
    initializeAnnotatedCodeMaps();

  if (document.readyState === "complete")
    moveToSelectedFunction();
  else
    window.addEventListener("load", moveToSelectedFunction, { once: true });
})();
