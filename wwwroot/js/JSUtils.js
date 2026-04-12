function applyStyleForElement(data) {
    document.getElementById(data.id).style[data.attrib] = data.value;
}

function applyStyleForElementClass(data) {
    for (let classReference of document.getElementsByClassName(data.className)) {
        classReference.style[data.attrib] = data.value;
    }   
}

function setInnerHTMLForElement(data) {
    document.getElementById(data.id).innerHTML = data.value;
}

function disableElement(data) {
    document.getElementById(data.id).disabled = data.value;
}

function BlazorDownloadFile(filename, contentType, content) {
    // Create the URL
    const file = new File([content], filename, { type: contentType });
    const exportUrl = URL.createObjectURL(file);

    // Create the <a> element and click on it
    const a = document.createElement("a");
    document.body.appendChild(a);
    a.href = exportUrl;
    a.download = filename;
    a.target = "_self";
    a.click();

    // We don't need to keep the object URL, let's release the memory
    // On older versions of Safari, it seems you need to comment this line...
    URL.revokeObjectURL(exportUrl);
}

function copyToClipboard(text) {
    navigator.clipboard.writeText(text).then(function () {
        //console.log('Text copied to clipboard');
    }).catch(function (err) {
        console.error('Could not copy text: ', err);
    });
}