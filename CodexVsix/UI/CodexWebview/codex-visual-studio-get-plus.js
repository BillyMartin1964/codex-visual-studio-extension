(function () {
  'use strict';

  var PLUS_URL = 'https://chatgpt.com/?cta_tab=personal&highlight_plan=plus&source=unknown#pricing';
  var LINK_ATTRIBUTE = 'data-codex-vs-get-plus';

  function createLink() {
    var link = document.createElement('a');
    link.setAttribute(LINK_ATTRIBUTE, 'true');
    link.href = PLUS_URL;
    link.target = '_blank';
    link.rel = 'noopener noreferrer';
    link.textContent = 'Get Plus';
    link.style.cssText = 'display:block;margin-left:auto;width:max-content;white-space:nowrap;'
      + 'font-size:12px;color:#63a8f8;text-decoration:none;cursor:pointer;';
    link.addEventListener('mouseenter', function () { link.style.textDecoration = 'underline'; });
    link.addEventListener('mouseleave', function () { link.style.textDecoration = 'none'; });
    return link;
  }

  function ensureLink() {
    try {
      // The grid footer is the composer's control row; the flex footer below it is the "Work locally" row.
      var footers = document.querySelectorAll('[class*="_footer_"]');
      for (var i = 0; i < footers.length; i++) {
        var footer = footers[i];
        if (footer.classList.contains('grid') || footer.querySelector('[' + LINK_ATTRIBUTE + ']')) {
          continue;
        }

        footer.appendChild(createLink());
      }
    } catch (error) {
      console.error('[codex-vs-get-plus]', error);
    }
  }

  // The top (grid) row keeps the bundle's 440px rule; the bottom "Work locally" row only holds two items,
  // so its labels stay visible down to 200px instead (the bundle also hides them at 475/480px in Electron mode).
  var bottomRow = '[class*="_footer_"]:not(.grid) ';
  var labelSelector = ['_labelSm_', '_labelXs_', '_secondaryLabel_', '_secondaryChevron_']
    .map(function (name) { return bottomRow + '[class*="' + name + '"]'; })
    .join(',');
  var style = document.createElement('style');
  style.textContent = '@container composer-footer (width>200px){' + labelSelector + '{display:inline !important}}'
    + '@container composer-footer (width<=200px){' + labelSelector + '{display:none !important}}'
    + bottomRow + '> :not([' + LINK_ATTRIBUTE + ']){min-width:0;overflow:hidden}'
    + bottomRow + '> [' + LINK_ATTRIBUTE + ']{flex:0 0 auto !important}';
  (document.head || document.documentElement).appendChild(style);

  setInterval(ensureLink, 500);
  ensureLink();
})();
