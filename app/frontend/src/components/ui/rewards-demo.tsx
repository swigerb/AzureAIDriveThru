import { useEffect, useRef, useState } from "react";
import { Flame, Gift } from "lucide-react";
import { Button } from "./button";

export type RewardsDemoAction = "status" | "check_in" | "redeem" | "unredeem" | "complete" | "reset";

export interface RewardsDemoSnapshot {
    checkedIn: boolean;
    balance: number;
    rewardItem: string;
    rewardCost: number;
    pointsPerDollar: number;
    demoCodes: string[];
    redeemed: boolean;
    completed: boolean;
    canRedeem: boolean;
    pointsToEarn: number;
    pointsAwarded: number;
    discount: number;
    message: string;
}

export default function RewardsDemo({
    snapshot, connected, onAction, onReconnect
}: {
    snapshot?: RewardsDemoSnapshot | null;
    connected: boolean;
    onAction: (action: RewardsDemoAction, code?: string) => void | Promise<void>;
    onReconnect?: () => void | Promise<void>;
}) {
    const [code, setCode] = useState("");
    const [error, setError] = useState("");
    const [pending, setPending] = useState<string | null>(null);
    const busy = useRef(false);
    const codes = snapshot?.demoCodes ?? [];
    const primary = "bg-white text-brand-primary-strong hover:bg-white/90";

    useEffect(() => {
        busy.current = false;
        setPending(null);
        setError("");
        if (!connected) setCode("");
    }, [snapshot, connected]);

    useEffect(() => {
        if (!pending) return;
        const timer = window.setTimeout(() => {
            busy.current = false;
            setPending(null);
            setError("No confirmation received. Check the ticket and balance before trying again.");
        }, 10000);
        return () => window.clearTimeout(timer);
    }, [pending]);

    async function run(label: string, action: () => void | Promise<void>) {
        if (busy.current) return;
        busy.current = true;
        setPending(label);
        setError("");
        try {
            await action();
        } catch {
            busy.current = false;
            setPending(null);
            setError("Could not send the request. Check your connection and try again.");
        }
    }

    return (
        <section className="rounded-3xl bg-brand-primary-strong p-5 text-white shadow-lg" aria-label="Simulated rewards">
            <div className="inline-flex items-center gap-2 rounded-full bg-white px-4 py-2 text-sm font-bold text-brand-primary-strong">
                <Flame aria-hidden="true" size={18} /> Rewards · demo only
            </div>
            <h2 className="mt-4 text-2xl font-black">Check in. Earn. Enjoy.</h2>
            <p className="mt-2 text-sm">Fictional points only. No real account, payment or purchase. Never enter real member information.</p>
            {!connected ? (
                <div className="mt-3">
                    <p className="text-sm">Session offline. An expired session starts with a fresh ticket and balance; check in again after reconnecting.</p>
                    {onReconnect && <Button disabled={Boolean(pending)} onClick={() => void run("Reconnecting…", onReconnect)} className={`mt-2 ${primary}`}>Reconnect</Button>}
                </div>
            ) : !snapshot ? (
                <Button disabled={Boolean(pending)} className={`mt-3 ${primary}`} onClick={() => void run("Loading…", () => onAction("status"))}>Load rewards</Button>
            ) : !snapshot.checkedIn ? (
                <form className="mt-4 space-y-2" onSubmit={event => {
                    event.preventDefault();
                    const candidate = code.trim().toUpperCase();
                    if (!codes.includes(candidate)) {
                        setError("Use only a fictional demo code shown below. No value was sent.");
                        return;
                    }
                    setError("");
                    setCode("");
                    void run("Checking in…", () => onAction("check_in", candidate));
                }}>
                    <label htmlFor="demo-rewards-code" className="block text-sm font-bold">Demo rewards / check-in code</label>
                    <input id="demo-rewards-code" autoComplete="off" spellCheck={false} maxLength={16}
                        value={code} onChange={event => setCode(event.target.value)} disabled={Boolean(pending)}
                        aria-describedby="demo-code-help" aria-invalid={Boolean(error)}
                        className="w-full rounded-xl border-2 border-white bg-white px-3 py-2 text-slate-950 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-white" />
                    <p id="demo-code-help" className="text-xs">
                        {codes.join(" or ")}. DEMO-PLUS starts with 500 fictional points; DEMO-NEW starts with 0.
                    </p>
                    <Button type="submit" disabled={Boolean(pending)} className={primary}>Check in</Button>
                </form>
            ) : (
                <div className="mt-4 space-y-3">
                    <p className="text-sm font-bold">{snapshot.completed ? "Order complete" : "Demo check-in linked to this order"}</p>
                    <p className="text-3xl font-black">{snapshot.balance} <span className="text-sm">demo points</span></p>
                    {snapshot.completed ? (
                        <>
                            <p className="text-sm">Earned {snapshot.pointsAwarded} fictional points. No purchase was placed.</p>
                            <Button disabled={Boolean(pending)} onClick={() => void run("Starting next order…", () => onAction("reset"))} className={primary}>Next order</Button>
                        </>
                    ) : (
                        <>
                            <p className="text-sm">Earn {snapshot.pointsToEarn} points on completion · {snapshot.pointsPerDollar} per paid pre-tax demo dollar, rounded down.</p>
                            <div className="rounded-2xl border border-white/50 p-3">
                                <p className="flex items-center gap-2 font-bold"><Gift aria-hidden="true" size={18} /> {snapshot.rewardItem} · {snapshot.rewardCost} demo points</p>
                                <p className="mt-1 text-sm">
                                    {snapshot.redeemed ? "One item covered. Points reserved until completion." :
                                        snapshot.canRedeem ? "You have an eligible reward! Use it on this order." :
                                            snapshot.balance >= snapshot.rewardCost ? `Add a plain ${snapshot.rewardItem} to this order to use the sample reward.` :
                                                "Keep earning demo points to unlock the sample reward."}
                                </p>
                                {snapshot.redeemed ? (
                                    <Button variant="link" disabled={Boolean(pending)} onClick={() => void run("Removing reward…", () => onAction("unredeem"))}
                                        className="mt-2 h-auto p-0 text-white underline">Undo reward</Button>
                                ) : snapshot.canRedeem && (
                                    <Button disabled={Boolean(pending)} onClick={() => void run("Applying reward…", () => onAction("redeem"))}
                                        className={`mt-3 ${primary}`}>Use reward</Button>
                                )}
                            </div>
                            <Button disabled={Boolean(pending)} onClick={() => void run("Completing order…", () => onAction("complete"))}
                                className={snapshot.canRedeem && !snapshot.redeemed ? "border-2 border-white bg-transparent text-white hover:bg-white/10" : primary}>Complete order</Button>
                        </>
                    )}
                </div>
            )}
            {error && <p role="alert" className="mt-3 text-sm font-bold">{error}</p>}
            <p role="status" aria-live="polite" className="mt-3 text-sm">{pending ?? (connected && !snapshot?.completed ? snapshot?.message : "")}</p>
            <p className="mt-3 text-xs">Sample prices and 8% tax. One fictional reward per order. Session-only points; no account sync.</p>
        </section>
    );
}
