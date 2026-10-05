// Petites fonctions appelées depuis Blazor (JS interop).
window.rl = {
  copy(text) {
    return navigator.clipboard.writeText(text);
  },
  download(filename, content) {
    const url = URL.createObjectURL(new Blob([content], { type: 'application/json' }));
    const a = Object.assign(document.createElement('a'), { href: url, download: filename });
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 5000);
  },
};
