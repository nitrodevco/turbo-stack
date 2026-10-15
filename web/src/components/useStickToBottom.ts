import { type DependencyList, useEffect, useRef } from 'react';

/**
 * Keeps a scrolling log box at its newest line as lines arrive, while the reader is at its bottom;
 * scrolled up to read, it stays where it is. Only the box scrolls, never the page around it
 * (`scrollIntoView` would move every scrolling ancestor, the window too). Attach the returned ref
 * to the box.
 */
export function useStickToBottom<T extends HTMLElement>(deps: DependencyList) {
    const ref = useRef<T>(null);
    const pinned = useRef(true);

    useEffect(() => {
        const box = ref.current;

        if (!box) return;

        const onScroll = () => {
            pinned.current = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
        };

        box.addEventListener('scroll', onScroll, { passive: true });

        return () => box.removeEventListener('scroll', onScroll);
    }, []);

    useEffect(() => {
        const box = ref.current;

        if (box && pinned.current) box.scrollTop = box.scrollHeight;
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, deps);

    return ref;
}
