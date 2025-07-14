window.fetchAndSave = async function (url, fullPath) {
    const resp = await fetch(url);
    if (!resp.ok) throw new Error(resp.status + " " + resp.statusText);

    const blob = await resp.blob();
    const a = document.createElement("a");
    const filename = fullPath.replace(/^.*[\\/]/, '')
    a.href = URL.createObjectURL(blob);
    a.download = filename;
    a.style.display = "none";
    document.body.appendChild(a);
    a.click();
    URL.revokeObjectURL(a.href);
    a.remove();
};