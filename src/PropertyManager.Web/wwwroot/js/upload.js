// Uploads files from forms marked data-file-upload.
//
// Each file is read into memory before anything is sent. On macOS, OneDrive and iCloud keep
// "online-only" placeholder files whose contents may not be readable; Safari then fails partway
// through a normal form post with "request body stream exhausted". Reading first lets us name the
// file and explain how to fix it instead of leaving the page.
document.addEventListener('submit', async (event) => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement) || !form.hasAttribute('data-file-upload')) {
        return;
    }
    event.preventDefault();

    const input = form.querySelector('input[type=file]');
    const button = form.querySelector('button[type=submit]');
    const message = form.querySelector('[data-upload-message]');
    const show = (text) => {
        message.textContent = text;
        message.hidden = !text;
    };

    show('');
    const label = button.textContent;
    button.disabled = true;
    button.textContent = 'Uploading…';
    try {
        const data = new FormData();
        for (const field of form.querySelectorAll('input:not([type=file]), select, textarea')) {
            if (field.name && !((field.type === 'checkbox' || field.type === 'radio') && !field.checked)) {
                data.append(field.name, field.value);
            }
        }

        const unreadable = [];
        for (const file of input.files) {
            try {
                const bytes = await file.arrayBuffer();
                if (bytes.byteLength !== file.size) {
                    throw new Error('incomplete read');
                }
                data.append(input.name, new Blob([bytes], { type: file.type }), file.name);
            } catch {
                unreadable.push(file.name);
            }
        }

        if (unreadable.length > 0) {
            show(`Couldn't read ${unreadable.map(n => `"${n}"`).join(', ')}. ` +
                'It is probably an online-only OneDrive or iCloud file that hasn\'t been downloaded to this Mac. ' +
                'In Finder, right-click it and choose "Always Keep on This Device", wait for the checkmark, then try again. ' +
                'Or download it from onedrive.com and upload the copy in your Downloads folder.');
            return;
        }

        const response = await fetch(form.action, { method: 'POST', body: data });
        if (!response.ok) {
            show(`Upload failed (${response.status} ${response.statusText}). Please try again.`);
            return;
        }

        // The server redirects back to the page with the result in the query string.
        const target = new URL(response.url);
        if (target.pathname + target.search === location.pathname + location.search) {
            location.reload();
        } else {
            location.href = target.pathname + target.search + (form.dataset.returnHash ?? '#documents');
        }
    } catch (error) {
        show(`Upload failed: ${error.message}. Check that the app is still running, then try again.`);
    } finally {
        button.disabled = false;
        button.textContent = label;
    }
});
