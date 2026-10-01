/*
 * MarkdownLite reader.js —— 应用自带的唯一"受控脚本"，只做五件事：
 * 1. 用离线 highlight.js 给代码块上色；
 * 2. 切换亮/暗主题（含跟随系统）；
 * 3. 把快捷键经 postMessage 桥回宿主（解决 WebView2 获焦时 WPF 收不到按键的问题）；
 * 4. 上报阅读位置、执行页内搜索（宿主搜索栏的页面侧实现）；
 * 5. 选中内容后右键直接复制（宿主出于安全禁用了默认右键菜单）。
 * 不读取、不外发任何文档内容；CSP 的 connect-src 'none' 保证页面无法联网。
 */
(function () {
    "use strict";

    var themeMode = "auto";

    function prefersDark() {
        return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
    }

    function isDark() {
        return themeMode === "dark" || (themeMode === "auto" && prefersDark());
    }

    function applyThemeClasses() {
        var dark = isDark();
        var light = document.getElementById("hljs-light");
        var darkLink = document.getElementById("hljs-dark");
        if (light) light.disabled = dark;
        if (darkLink) darkLink.disabled = !dark;
    }

    // 由宿主通过 ExecuteScriptAsync 调用
    window.mdApplyTheme = function (mode) {
        if (mode !== "light" && mode !== "dark" && mode !== "auto") return;
        themeMode = mode;
        document.documentElement.setAttribute("data-theme", mode);
        applyThemeClasses();
    };

    // 由宿主在点击目录条目 / 跳转跨文档锚点时调用（参数由宿主做 JSON 序列化，无注入面）
    window.mdScrollTo = function (id) {
        var el = document.getElementById(id);
        if (el && el.scrollIntoView) {
            el.scrollIntoView({ behavior: "smooth", block: "start" });
        }
    };

    function postToHost(message) {
        if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
            window.chrome.webview.postMessage(message);
            return true;
        }
        return false;
    }

    /* ================================================================
     * 右键菜单：宿主禁用了 WebView2 默认右键菜单（避免暴露「新窗口打开」
     * 等不安全入口）。选中文字后按右键，把**坐标**（不含文本，避免选区
     * 内容进宿主诊断日志）报给宿主，由宿主在鼠标处弹应用风格菜单，
     * 用户点「复制」时宿主再经 ExecuteScriptAsync 取回选区写入剪贴板。
     * ================================================================ */
    document.addEventListener("contextmenu", function (event) {
        var selection = window.getSelection ? window.getSelection() : null;
        var text = selection ? String(selection.toString()) : "";
        if (!text) return; // 没有选中内容：不弹菜单
        if (event.cancelable) event.preventDefault();
        postToHost("ctx:" + Math.round(event.clientX) + ":" + Math.round(event.clientY));
    });

    /* ================================================================
     * 阅读位置：滚动比例 + 当前章节，供宿主恢复进度与高亮侧栏
     * ================================================================ */

    function scrollRatio() {
        var doc = document.documentElement;
        var max = doc.scrollHeight - window.innerHeight;
        if (max <= 0) return 0;
        var top = window.pageYOffset || doc.scrollTop || 0;
        return Math.min(1, Math.max(0, top / max));
    }

    function headingElements() {
        return document.querySelectorAll("h1[id],h2[id],h3[id],h4[id],h5[id],h6[id]");
    }

    // 视口顶部之下最后出现过的标题，即为"当前章节"
    function currentHeadingId() {
        var list = headingElements();
        var current = "";
        for (var i = 0; i < list.length; i++) {
            if (list[i].getBoundingClientRect().top > 96) break;
            current = list[i].id;
        }
        return current;
    }

    var lastReport = 0;

    function reportPosition(force) {
        var now = Date.now();
        if (!force && now - lastReport < 250) return;
        lastReport = now;
        postToHost("scroll:" + scrollRatio().toFixed(5) + ":" + currentHeadingId());
    }

    // 由宿主通过 ExecuteScriptAsync 调用，恢复上次阅读位置；比例 <= 0 时不做任何事
    window.mdScrollToRatio = function (ratio) {
        if (typeof ratio !== "number" || ratio <= 0) return;
        var doc = document.documentElement;
        var max = doc.scrollHeight - window.innerHeight;
        if (max <= 0) return;
        window.scrollTo(0, max * Math.min(1, ratio));
    };

    window.addEventListener("scroll", function () { reportPosition(false); }, { passive: true });
    window.addEventListener("resize", function () { reportPosition(true); });

    /* ================================================================
     * 页内搜索：命中高亮 / 上下条跳转 / 计数上报
     * ================================================================ */

    var hits = [];
    var hitIndex = -1;

    function clearSearch() {
        var marks = document.querySelectorAll("mark.mdreader-hit");
        for (var i = 0; i < marks.length; i++) {
            var mark = marks[i];
            var parent = mark.parentNode;
            if (!parent) continue;
            parent.replaceChild(document.createTextNode(mark.textContent), mark);
            parent.normalize();
        }
        hits = [];
        hitIndex = -1;
    }

    // 只取可搜索的文本节点：跳过脚本 / 样式 / 已高亮片段 / 纯空白
    function searchableTextNodes() {
        var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
            acceptNode: function (node) {
                if (!node.nodeValue || !node.nodeValue.trim()) return NodeFilter.FILTER_REJECT;
                var name = node.parentNode ? node.parentNode.nodeName : "";
                if (name === "SCRIPT" || name === "STYLE" || name === "MARK") return NodeFilter.FILTER_REJECT;
                return NodeFilter.FILTER_ACCEPT;
            }
        });

        var nodes = [];
        var node;
        while ((node = walker.nextNode())) nodes.push(node);
        return nodes;
    }

    function reportSearch() {
        postToHost("search:" + hits.length + ":" + (hitIndex >= 0 ? hitIndex + 1 : 0));
    }

    function activateHit(index) {
        for (var i = 0; i < hits.length; i++) {
            hits[i].classList.toggle("mdreader-hit-current", i === index);
        }
        if (hits[index] && hits[index].scrollIntoView) {
            hits[index].scrollIntoView({ behavior: "smooth", block: "center" });
        }
        reportSearch();
    }

    window.mdSearch = function (query) {
        clearSearch();
        if (!query) { reportSearch(); return; }

        var needle = query.toLowerCase();
        var nodes = searchableTextNodes();

        for (var i = 0; i < nodes.length; i++) {
            var node = nodes[i];
            var text = node.nodeValue;
            var haystack = text.toLowerCase();
            var from = 0;
            var index;
            var fragment = null;

            while ((index = haystack.indexOf(needle, from)) !== -1) {
                if (!fragment) fragment = document.createDocumentFragment();
                if (index > from) fragment.appendChild(document.createTextNode(text.slice(from, index)));
                var mark = document.createElement("mark");
                mark.className = "mdreader-hit";
                mark.textContent = text.slice(index, index + query.length);
                fragment.appendChild(mark);
                from = index + query.length;
            }

            if (fragment) {
                if (from < text.length) fragment.appendChild(document.createTextNode(text.slice(from)));
                node.parentNode.replaceChild(fragment, node);
            }
        }

        hits = document.querySelectorAll("mark.mdreader-hit");
        hitIndex = hits.length > 0 ? 0 : -1;
        if (hitIndex === 0) activateHit(0);
        else reportSearch();
    };

    window.mdSearchStep = function (delta) {
        if (hits.length === 0) return;
        hitIndex = (hitIndex + delta + hits.length) % hits.length;
        activateHit(hitIndex);
    };

    /* ================================================================
     * 快捷键桥：页面有焦点时由这里转发
     * ================================================================ */

    document.addEventListener("keydown", function (e) {
        if (e.key === "Escape") {
            postToHost("shortcut:escape");
            return;
        }
        if (e.altKey && !e.ctrlKey && !e.metaKey && e.key === "ArrowLeft") {
            // Alt+← 返回上一文档（浏览器导航快捷键已被宿主禁用，这里桥回宿主处理）
            e.preventDefault();
            postToHost("shortcut:back");
            return;
        }
        if (e.key === "F5" && !e.ctrlKey && !e.shiftKey && !e.altKey) {
            e.preventDefault();
            postToHost("shortcut:reload");
            return;
        }
        if (!e.ctrlKey || e.altKey || e.metaKey) return;
        if (e.key === "r" || e.key === "R") {
            e.preventDefault();
            postToHost("shortcut:reload");
            return;
        }
        switch (e.key) {
            case "o": case "O":
                e.preventDefault(); postToHost("shortcut:open"); return;
            case "f": case "F":
                e.preventDefault(); postToHost("shortcut:find"); return;
            case "t": case "T":
                e.preventDefault(); postToHost("shortcut:toggle-toc"); return;
            case "=": case "+":
                e.preventDefault(); postToHost("shortcut:zoom-in"); return;
            case "-": case "_":
                e.preventDefault(); postToHost("shortcut:zoom-out"); return;
            case "0":
                e.preventDefault(); postToHost("shortcut:zoom-reset"); return;
        }
        if (e.code === "NumpadAdd") { e.preventDefault(); postToHost("shortcut:zoom-in"); }
        else if (e.code === "NumpadSubtract") { e.preventDefault(); postToHost("shortcut:zoom-out"); }
        else if (e.code === "Numpad0") { e.preventDefault(); postToHost("shortcut:zoom-reset"); }
    });

    if (window.matchMedia) {
        var mq = window.matchMedia("(prefers-color-scheme: dark)");
        if (mq.addEventListener) mq.addEventListener("change", applyThemeClasses);
    }

    document.addEventListener("DOMContentLoaded", function () {
        applyThemeClasses();

        if (window.hljs) {
            var blocks = document.querySelectorAll("pre code");
            for (var i = 0; i < blocks.length; i++) {
                try {
                    window.hljs.highlightElement(blocks[i]);
                } catch (err) {
                    /* 单个代码块高亮失败不影响阅读 */
                }
            }
        }

        reportPosition(true);
    });

    /* ================================================================
     * 滚动性能采样（--diag-scroll / --diag-memory 专用，平时不会被调用）
     *
     * mdScrollPerfTest(durationMs, stepRatio)：
     *   用 requestAnimationFrame 循环「每帧滚一小步」，同时按秒计帧——
     *   滚动与采样在同一条 rAF 链上，测的就是真实滚动时的合成帧率。
     *   结束后把「总时长 + 每秒帧数列表」回传宿主，由 C# 侧统计
     *   平均 FPS / 最低 FPS / 掉帧数。
     *
     * 回传格式：scrollperf:<实际时长ms>:<第1秒帧数>,<第2秒帧数>,…
     * ================================================================ */
    window.mdScrollPerfTest = function (durationMs, stepRatio) {
        var doc = document.documentElement;
        var max = doc.scrollHeight - window.innerHeight;
        var perSecond = [];
        var frames = 0;
        var secondStart = 0;
        var start = 0;
        var pos = 0;
        var dir = 1;
        var step = Math.max(4, Math.round(max * (stepRatio || 0.05)));

        function tick(ts) {
            if (!start) { start = ts; secondStart = ts; }
            frames++;

            if (ts - secondStart >= 1000) {
                perSecond.push(frames);
                frames = 0;
                secondStart = ts;
            }

            if (ts - start >= durationMs) {
                perSecond.push(frames);              // 最后不足一秒的尾巴
                postToHost("scrollperf:" + (ts - start) + ":" + perSecond.join(","));
                return;
            }

            if (max > 0) {
                pos += step * dir;
                if (pos >= max) { pos = max; dir = -1; }
                if (pos <= 0) { pos = 0; dir = 1; }
                window.scrollTo(0, pos);
            }
            requestAnimationFrame(tick);
        }
        requestAnimationFrame(tick);
    };
})();
